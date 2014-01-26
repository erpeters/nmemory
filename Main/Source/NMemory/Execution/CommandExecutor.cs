﻿// ----------------------------------------------------------------------------------
// <copyright file="CommandExecutor.cs" company="NMemory Team">
//     Copyright (C) 2012-2013 NMemory Team
//
//     Permission is hereby granted, free of charge, to any person obtaining a copy
//     of this software and associated documentation files (the "Software"), to deal
//     in the Software without restriction, including without limitation the rights
//     to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//     copies of the Software, and to permit persons to whom the Software is
//     furnished to do so, subject to the following conditions:
//
//     The above copyright notice and this permission notice shall be included in
//     all copies or substantial portions of the Software.
//
//     THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//     IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//     FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//     AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//     LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//     OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//     THE SOFTWARE.
// </copyright>
// ----------------------------------------------------------------------------------

namespace NMemory.Execution
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using NMemory.Common;
    using NMemory.Indexes;
    using NMemory.Modularity;
    using NMemory.Tables;
    using NMemory.Transactions.Logs;
    using NMemory.Utilities;

    public class CommandExecutor : ICommandExecutor, IDeletePrimitive
    {
        private IDatabase database;

        public void Initialize(IDatabase database)
        {
            this.database = database;
        }

        #region Query

        public IEnumerator<T> ExecuteQuery<T>(
            IExecutionPlan<IEnumerable<T>> plan, 
            IExecutionContext context)
        {
            ITable[] tables = TableLocator.FindAffectedTables(context.Database, plan);

            return ExecuteQuery(plan, context, tables, cloneEntities: true);
        }

         private IEnumerator<T> ExecuteQuery<T>(
            IExecutionPlan<IEnumerable<T>> plan, 
            IExecutionContext context,
            ITable[] tablesToLock,
            bool cloneEntities)
        {
            ITable[] tables = TableLocator.FindAffectedTables(context.Database, plan);

            EntityPropertyCloner<T> cloner = null;
            if (cloneEntities && this.database.Tables.IsEntityType<T>())
            {
                cloner = EntityPropertyCloner<T>.Instance;
            }

            LinkedList<T> result = new LinkedList<T>();

            for (int i = 0; i < tablesToLock.Length; i++)
            {
                this.AcquireReadLock(tablesToLock[i], context);
            }

            IEnumerable<T> query = plan.Execute(context);

            try
            {
                foreach (T item in query)
                {
                    if (cloner != null)
                    {
                        T resultEntity = Activator.CreateInstance<T>();
                        cloner.Clone(item, resultEntity);

                        result.AddLast(resultEntity);
                    }
                    else
                    {
                        result.AddLast(item);
                    }
                }
            }
            finally
            {
                for (int i = 0; i < tablesToLock.Length; i++)
                {
                    this.ReleaseReadLock(tablesToLock[i], context);
                }
            }

            return result.GetEnumerator();
        }

        public T ExecuteQuery<T>(
            IExecutionPlan<T> plan, 
            IExecutionContext context)
        {
            ITable[] tables = TableLocator.FindAffectedTables(context.Database, plan);

            for (int i = 0; i < tables.Length; i++)
            {
                this.AcquireReadLock(tables[i], context);
            }

            try
            {
                return plan.Execute(context);
            }
            finally
            {
                for (int i = 0; i < tables.Length; i++)
                {
                    this.ReleaseReadLock(tables[i], context);
                }
            }
        }

        #endregion

        #region Insert
        
        public void ExecuteInsert<T>(T entity, IExecutionContext context) 
            where T : class
        {
            ITable<T> table = this.Database.Tables.FindTable<T>();

            table.Contraints.Apply(entity, context);

            // Find referred relations
            // Do not add referring relations!
            RelationGroup relations = this.FindRelations(table.Indexes, referring: false);

            // Acquire locks
            this.AcquireWriteLock(table, context);
            this.LockRelatedTables(relations, context, table);

            try
            {
                // Validate the inserted record
                this.ValidateForeignKeys(relations.Referred, new[] { entity });

                using (AtomicLogScope logScope = this.StartAtomicLogOperation(context))
                {
                    foreach (IIndex<T> index in table.Indexes)
                    {
                        index.Insert(entity);
                        logScope.Log.WriteIndexInsert(index, entity);
                    }

                    logScope.Complete();
                }
            }
            finally
            {
                this.ReleaseWriteLock(table, context);
            }
        }

        #endregion

        #region Delete
       
        public IEnumerable<T> ExecuteDelete<T>(
            IExecutionPlan<IEnumerable<T>> plan, 
            IExecutionContext context) 
            where T : class
        {
            var table = this.Database.Tables.FindTable<T>();
            var cascadedTables = this.GetCascadedTables(table);
            var allTables = cascadedTables.Concat(new[] { table }).ToArray();

            // Find relations
            // Do not add referred relations!
            RelationGroup allRelations = 
                this.FindRelations(allTables.SelectMany(x => x.Indexes), referred: false);

            this.AcquireWriteLock(table, context);

            var storedEntities = this.Query(plan, table, context);

            this.AcquireWriteLock(cascadedTables, context);
            this.LockRelatedTables(allRelations, context, except: allTables);

            using (AtomicLogScope logScope = this.StartAtomicLogOperation(context))
            {
                ((IDeletePrimitive)this).Delete<T>(storedEntities, logScope);

                logScope.Complete();
            }

            return storedEntities.ToArray();
        }

        void IDeletePrimitive.Delete<T>(
            IList<T> storedEntities, 
            AtomicLogScope log)
        {
            ITable<T> table = this.Database.Tables.FindTable<T>();

            // Find relations
            // Do not add referred relations!
            RelationGroup relations =
                this.FindRelations(table.Indexes, referred: false);

            // Find referring entities
            var referringEntities =
                this.FindReferringEntities<T>(storedEntities, relations.Referring);

            // Delete invalid index records
            for (int i = 0; i < storedEntities.Count; i++)
            {
                T storedEntity = storedEntities[i];

                foreach (IIndex<T> index in table.Indexes)
                {
                    index.Delete(storedEntity);
                    log.Log.WriteIndexDelete(index, storedEntity);
                }
            }

            var cascadedRelations = relations.Referring.Where(x => x.Options.CascadedDeletion);

            foreach (IRelationInternal index in cascadedRelations)
            {
                var entities = referringEntities[index];

                if (entities.Count == 0)
                {
                    continue;
                }

                index.CascadedDelete(entities, (IDeletePrimitive)this, log);

                // At this point these entities should have been removed from the database
                // In order to avoid foreign key validation, clear the collection
                //
                // TODO: It might be better to do foreign key validation from the
                // other direction: check if anything refers storedEntities
                entities.Clear();
            }

            // Validate the entities that are referring to the deleted entities
            this.ValidateForeignKeys(relations.Referring, referringEntities);
        }

        #endregion

        #region Update

        public IEnumerable<T> ExecuteUpdater<T>(
            IExecutionPlan<IEnumerable<T>> plan,
            IUpdater<T> updater,
            IExecutionContext context)
            where T : class
        {
            ITable<T> table = this.Database.Tables.FindTable<T>();
            var cloner = EntityPropertyCloner<T>.Instance;

            // Determine which indexes are affected by the change
            // If the key of an index containes a changed property, it is affected
            IList<IIndex<T>> affectedIndexes = FindAffectedIndexes(table, updater.Changes);

            // Find relations
            // Add both referring and referred relations!
            RelationGroup relations = this.FindRelations(affectedIndexes);

            this.AcquireWriteLock(table, context);

            var storedEntities = Query(plan, table, context);

            // Lock related tables (based on found relations)
            this.LockRelatedTables(relations, context, table);

            // Find the entities referring the entities that are about to be updated
            var referringEntities =
                this.FindReferringEntities(storedEntities, relations.Referring);

            using (AtomicLogScope logScope = this.StartAtomicLogOperation(context))
            {
                // Delete invalid index records (keys are invalid)
                for (int i = 0; i < storedEntities.Count; i++)
                {
                    T storedEntity = storedEntities[i];

                    foreach (IIndex<T> index in affectedIndexes)
                    {
                        index.Delete(storedEntity);
                        logScope.Log.WriteIndexDelete(index, storedEntity);
                    }
                }

                // Modify entity properties
                for (int i = 0; i < storedEntities.Count; i++)
                {
                    T storedEntity = storedEntities[i];

                    // Create backup
                    T backup = Activator.CreateInstance<T>();
                    cloner.Clone(storedEntity, backup);
                    T newEntity = updater.Update(storedEntity);

                    // Apply contraints on the entity
                    table.Contraints.Apply(newEntity, context);

                    // Update entity
                    cloner.Clone(newEntity, storedEntity);
                    logScope.Log.WriteEntityUpdate(cloner, storedEntity, backup);
                }

                // Insert to indexes the entities were removed from
                for (int i = 0; i < storedEntities.Count; i++)
                {
                    T storedEntity = storedEntities[i];

                    foreach (IIndex<T> index in affectedIndexes)
                    {
                        index.Insert(storedEntity);
                        logScope.Log.WriteIndexInsert(index, storedEntity);
                    }
                }

                // Validate the updated entities
                this.ValidateForeignKeys(relations.Referred, storedEntities);

                // Validate the entities that were referring to the old version of entities
                this.ValidateForeignKeys(relations.Referring, referringEntities);

                logScope.Complete();
            }

            return storedEntities;
        }

        #endregion

        protected IDatabase Database
        {
            get { return this.database; }
        }

        protected IConcurrencyManager ConcurrencyManager
        {
            get { return this.Database.DatabaseEngine.ConcurrencyManager; }
        }

        #region Locking

        protected void AcquireWriteLock(ITable table, IExecutionContext context)
        {
            this.ConcurrencyManager.AcquireTableWriteLock(table, context.Transaction);
        }

        protected void AcquireWriteLock(ITable[] tables, IExecutionContext context)
        {
            for (int i = 0; i < tables.Length; i++)
            {
                this.AcquireWriteLock(tables[i], context);
            }
        }

        protected void ReleaseWriteLock(ITable table, IExecutionContext context)
        {
            this.ConcurrencyManager.ReleaseTableWriteLock(table, context.Transaction);
        }

        protected void AcquireReadLock(ITable table, IExecutionContext context)
        {
            this.ConcurrencyManager.AcquireTableReadLock(table, context.Transaction);
        }

        protected void ReleaseReadLock(ITable table, IExecutionContext context)
        {
            this.ConcurrencyManager.ReleaseTableReadLock(table, context.Transaction);
        }

        protected void LockRelatedTables(
            ITable[] relatedTables,
            IExecutionContext context)
        {
            for (int i = 0; i < relatedTables.Length; i++)
            {
                this.ConcurrencyManager
                    .AcquireRelatedTableLock(relatedTables[i], context.Transaction);
            }
        }

        private void LockRelatedTables(
            RelationGroup relations,
            IExecutionContext context,
            params ITable[] except)
        {
            ITable[] relatedTables = this.GetRelatedTables(relations, except).ToArray();

            this.LockRelatedTables(relatedTables, context);
        }

        #endregion

        #region Relations

        private IEnumerable<ITable> GetRelatedTables(
            RelationGroup relations, 
            params ITable[] except)
        {
            return
                relations.Referring.Select(x => x.ForeignTable)
                .Concat(relations.Referred.Select(x => x.PrimaryTable))
                .Distinct()
                .Except(except);
        }

        private RelationGroup FindRelations(
           IEnumerable<IIndex> indexes,
           bool referring = true,
           bool referred = true)
        {
            RelationGroup relations = new RelationGroup();

            foreach (IIndex index in indexes)
            {
                if (referring)
                {
                    foreach (var relation in this.Database.Tables.GetReferringRelations(index))
                    {
                        if (!relations.Referring.Contains(relation))
                        {
                            relations.Referring.Add(relation);
                        }
                    }
                }

                if (referred)
                {
                    foreach (var relation in this.Database.Tables.GetReferredRelations(index))
                    {
                        if (!relations.Referred.Contains(relation))
                        {
                            relations.Referred.Add(relation);
                        }
                    }
                }
            }

            return relations;
        }

        private Dictionary<IRelation, HashSet<object>> FindReferringEntities<T>(
            IList<T> storedEntities,
            IList<IRelationInternal> relations)
            where T : class
        {
            var result = new Dictionary<IRelation, HashSet<object>>();

            for (int j = 0; j < relations.Count; j++)
            {
                IRelationInternal relation = relations[j];

                HashSet<object> reffering = new HashSet<object>();

                for (int i = 0; i < storedEntities.Count; i++)
                {
                    foreach (object entity in relation.GetReferringEntities(storedEntities[i]))
                    {
                        reffering.Add(entity);
                    }
                }

                result.Add(relation, reffering);
            }

            return result;
        }

        private void ValidateForeignKeys(
           IList<IRelationInternal> relations,
           Dictionary<IRelation, HashSet<object>> referringEntities)
        {
            for (int i = 0; i < relations.Count; i++)
            {
                IRelationInternal relation = relations[i];

                foreach (object entity in referringEntities[relation])
                {
                    relation.ValidateEntity(entity);
                }
            }
        }

        private void ValidateForeignKeys(
            IList<IRelationInternal> relations,
            IEnumerable<object> referringEntities)
        {
            if (relations.Count == 0)
            {
                return;
            }

            foreach (object entity in referringEntities)
            {
                for (int i = 0; i < relations.Count; i++)
                {
                    relations[i].ValidateEntity(entity);
                }
            }
        }

        private ITable[] GetCascadedTables(ITable table)
        {
            List<ITable> tables = new List<ITable>();

            CollectAllCascadedTables(table, tables);

            return tables
                .Except(new[] { table })
                .ToArray();
        }

        private void CollectAllCascadedTables(ITable currentTable, List<ITable> tables)
        {
            var relations = this.FindRelations(currentTable.Indexes, referred: false)
                .Referring;

            var referringTables = relations
                .Where(x => x.Options.CascadedDeletion)
                .Select(x => x.ForeignTable)
                .ToList();

            foreach (ITable table in referringTables)
            {
                if (!tables.Contains(table))
                {
                    tables.Add(table);
                    CollectAllCascadedTables(currentTable, tables);
                }
            }
        }

        #endregion

        private List<T> Query<T>(
            IExecutionPlan<IEnumerable<T>> plan,
            ITable<T> table,
            IExecutionContext context)
            where T : class
        {
            ITable[] queryTables = TableLocator.FindAffectedTables(context.Database, plan);

            var query = this.ExecuteQuery(
                plan,
                context,
                queryTables.Except(new[] { table }).ToArray(),
                cloneEntities: false);

            return query.ToEnumerable().ToList();
        }

        private IList<IIndex<T>> FindAffectedIndexes<T>(ITable<T> table, MemberInfo[] changes)
            where T : class
        {
            IList<IIndex<T>> affectedIndexes = new List<IIndex<T>>();

            foreach (IIndex<T> index in table.Indexes)
            {
                if (index.KeyInfo.EntityKeyMembers.Any(x => changes.Contains(x)))
                {
                    affectedIndexes.Add(index);
                }
            }
            return affectedIndexes;
        }

        private AtomicLogScope StartAtomicLogOperation(IExecutionContext context)
        {
            return new AtomicLogScope(context.Transaction, context.Database);
        }
    }
}