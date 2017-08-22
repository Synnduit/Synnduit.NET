﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Synnduit.Configuration;
using Synnduit.Mappings;
using Synnduit.Persistence;
using Synnduit.Serialization;

namespace Synnduit.Events
{
    /// <summary>
    /// Logs individual run events to the database. 
    /// </summary>
    /// <typeparam name="TEntity">The type representing the entity.</typeparam>
    [Export(typeof(IEntityTypeEventReceiver<>))]
    internal class DatabaseLogger<TEntity> : EventReceiver<TEntity>
        where TEntity : class
    {
        private const string
            SerializedDestinationSystemEntityDataKey = "SerializedDestinationSystemEntity";

        private static readonly EntityTransactionOutcome[]
            EntityTransactionOutcomesThatRecordSourceSystemEntity =
                new EntityTransactionOutcome[]
                {
                    EntityTransactionOutcome.NoChangesMerged,
                    EntityTransactionOutcome.ChangesDetectedAndMerged,
                    EntityTransactionOutcome.NotFoundInDestinationSystem,
                    EntityTransactionOutcome.DuplicateDetectedChangesMerged,
                    EntityTransactionOutcome.DuplicateDetectedNoChangesMerged,
                    //EntityTransactionOutcome.ReferredForManualDeduplication,
                                                    // when feature implemented
                    EntityTransactionOutcome.NewEntityCreated
                };

        private readonly IContext context;

        private readonly IOperationExecutive operationExecutive;

        private readonly IServiceProvider<TEntity> serviceProvider;

        private readonly IMappingDataRepository mappingDataRepository;

        private readonly IHashingSerializer<TEntity> hashingSerializer;

        private readonly IHashFunction hashFunction;

        private readonly ILoggingConfigurationProvider loggingConfigurationProvider;

        private readonly ISafeRepository safeRepository;

        [ImportingConstructor]
        public DatabaseLogger(
            IContext context,
            IOperationExecutive operationExecutive,
            IServiceProvider<TEntity> serviceProvider,
            IMappingDataRepository mappingDataRepository,
            IHashingSerializer<TEntity> hashingSerializer,
            IHashFunction hashFunction,
            ILoggingConfigurationProvider loggingConfigurationProvider,
            ISafeRepository safeRepository)
        {
            this.context = context;
            this.operationExecutive = operationExecutive;
            this.serviceProvider = serviceProvider;
            this.mappingDataRepository = mappingDataRepository;
            this.hashingSerializer = hashingSerializer;
            this.hashFunction = hashFunction;
            this.loggingConfigurationProvider = loggingConfigurationProvider;
            this.safeRepository = safeRepository;
        }

        /// <summary>
        /// Called when a source system entity has been processed.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnProcessed(IProcessedArgs<TEntity> args)
        {
            bool shouldRecordTransaction = this.ShouldRecordTransaction(args);
            bool hasMessages =
                args.LogMessages.Count() > 0
                || args.Exception != null;
            if(shouldRecordTransaction || hasMessages)
            {
                if(shouldRecordTransaction)
                {
                    this.RecordTransaction(args);
                }
                this.RecordSourceSystemEntity(args);
                this.RecordMessages(args, args.SourceSystemEntityId);
                this.operationExecutive
                    .CurrentOperation
                    .UpdateIdentityCorrelationId(args.SourceSystemEntityId);
            }
        }

        /// <summary>
        /// Called when a destination system entity identified for deletion has been loaded
        /// from the destination system.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnDeletionEntityLoaded(
            IDeletionEntityLoadedArgs<TEntity> args)
        {
            if(this.loggingConfigurationProvider.GarbageCollectionConfiguration.Entity)
            {
                this.operationExecutive
                    .CurrentOperation
                    .Data[SerializedDestinationSystemEntityDataKey]
                    = this.SerializeEntity(args.Entity);
            }
        }

        /// <summary>
        /// Called when the deletion of a destination system entity (identified for
        /// deletion) has been processed.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnDeletionProcessed(IDeletionProcessedArgs args)
        {
            if(this.ShouldRecordDeletion(args))
            {
                this.RecordDeletion(args);
                this.RecordDeletionDestinationSystemEntity();
                this.RecordMessages(args);
            }
        }

        private bool ShouldRecordTransaction(IProcessedArgs<TEntity> args)
        {
            bool shouldRecordTransaction = true;
            if(this
                .loggingConfigurationProvider
                .MigrationConfiguration
                .ExcludedOutcomes
                .Contains(args.Outcome))
            {
                if(!this
                    .loggingConfigurationProvider
                    .MigrationConfiguration
                    .AlwaysLogMessages
                    || args.LogMessages.Count() == 0)
                {
                    shouldRecordTransaction = false;
                }
            }
            return shouldRecordTransaction;
        }

        private void RecordTransaction(IProcessedArgs<TEntity> args)
        {
            EntityTransaction transaction = this.RecordTransactionEntry(args);
            this.RecordDestinationSystemEntity(transaction, args);
            this.RecordValueChanges(transaction, args);           
        }

        private EntityTransaction RecordTransactionEntry(IProcessedArgs<TEntity> args)
        {
            EntityTransaction transaction;
            Guid? mappingId =
                this
                .mappingDataRepository
                .GetMappingData(
                    this.context.EntityType.Id,
                    this.context.SourceSystem.Id,
                    args.SourceSystemEntityId)
                ?.MappingId;
            if(mappingId.HasValue)
            {
                var mappingEntityTransaction = new MappingEntityTransaction(
                    this.operationExecutive.CurrentOperation,
                    (int) args.Outcome,
                    mappingId.Value);
                this.safeRepository
                    .CreateMappingEntityTransaction(mappingEntityTransaction);
                transaction = mappingEntityTransaction;
            }
            else
            {
                var identityEntityTransaction = new IdentityEntityTransaction(
                    this.operationExecutive.CurrentOperation,
                    (int) args.Outcome,
                    this.context.EntityType.Id,
                    this.context.SourceSystem.Id,
                    args.SourceSystemEntityId);
                this.safeRepository
                    .CreateIdentityEntityTransaction(identityEntityTransaction);
                transaction = identityEntityTransaction;
            }
            return transaction;
        }

        private void RecordDestinationSystemEntity(
            EntityTransaction transaction, IProcessedArgs<TEntity> args)
        {
            if(this
                .loggingConfigurationProvider
                .MigrationConfiguration
                .DestinationSystemEntity
                && args.DestinationSystemEntity != null)
            {
                SerializedEntity serializedDestinationSystemEntity =
                    this.SerializeEntity(args.DestinationSystemEntity);
                if(serializedDestinationSystemEntity != null)
                {
                    this.safeRepository
                        .CreateOperationDestinationSystemEntity(
                            new OperationSerializedEntity(
                                transaction, serializedDestinationSystemEntity));
                }
            }
        }

        private void RecordValueChanges(
            EntityTransaction transaction, IProcessedArgs<TEntity> args)
        {
            if(this.loggingConfigurationProvider.MigrationConfiguration.ValueChanges)
            {
                foreach(ValueChange valueChange in args.ValueChanges)
                {
                    this.safeRepository.CreateValueChange(
                        new PersistenceValueChange(
                            Guid.NewGuid(),
                            transaction.Id,
                            valueChange.ValueName,
                            args.Outcome != EntityTransactionOutcome.NewEntityCreated
                                ? valueChange.PreviousValue?.ToString()
                                : null,
                            valueChange.NewValue?.ToString()));
                }
            }
        }

        private void RecordSourceSystemEntity(IProcessedArgs<TEntity> args)
        {
            if(this.loggingConfigurationProvider.MigrationConfiguration.SourceSystemEntity
                && args.SourceSystemEntity != null
                && !EntityTransactionOutcomesThatRecordSourceSystemEntity.Contains(args.Outcome))
            {
                SerializedEntity serializedEntity =
                    this.GetSerializedSourceSystemEntity(args);
                if(serializedEntity != null)
                {
                    if(args.SourceSystemEntityId != null)
                    {
                        var entity =
                            new IdentityOperationSourceSystemEntity(
                                this.operationExecutive.CurrentOperation,
                                this.context.EntityType.Id,
                                this.context.SourceSystem.Id,
                                args.SourceSystemEntityId,
                                serializedEntity);
                        this.safeRepository
                            .CreateIdentityOperationSourceSystemEntity(entity);
                    }
                    else
                    {
                        var entity = new OperationSerializedEntity(
                            this.operationExecutive.CurrentOperation,
                            serializedEntity);
                        this.safeRepository
                            .CreateOperationSourceSystemEntity(entity);
                    }
                }
            }
        }

        private SerializedEntity
            GetSerializedSourceSystemEntity(IProcessedArgs<TEntity> args)
        {
            SerializedEntity serializedEntity;
            if(args.SerializedSourceSystemEntity != null)
            {
                serializedEntity = new SerializedEntity(
                    args.SerializedSourceSystemEntity.DataHash,
                    args.SerializedSourceSystemEntity.Data,
                    args.SerializedSourceSystemEntity.Label);
            }
            else
            {
                serializedEntity = this.SerializeEntity(args.SourceSystemEntity);
            }
            return serializedEntity;
        }

        private bool ShouldRecordDeletion(IDeletionProcessedArgs args)
        {
            bool shouldRecordDeletion = true;
            if(this
                .loggingConfigurationProvider
                .GarbageCollectionConfiguration
                .ExcludedOutcomes
                .Contains(args.Outcome))
            {
                if(!this
                    .loggingConfigurationProvider
                    .GarbageCollectionConfiguration
                    .AlwaysLogMessages
                    || args.LogMessages.Count() == 0)
                {
                    shouldRecordDeletion = false;
                }
            }
            return shouldRecordDeletion;
        }

        private void RecordDeletion(IDeletionProcessedArgs args)
        {
            this.safeRepository.CreateEntityDeletion(new EntityDeletion(
                this.operationExecutive.CurrentOperation,
                this.context.EntityType.Id,
                args.EntityId,
                (int) args.Outcome));
        }

        private void RecordDeletionDestinationSystemEntity()
        {
            SerializedEntity serializedEntity;
            this.operationExecutive.CurrentOperation.Data.TryGetValue(
                SerializedDestinationSystemEntityDataKey,
                out object serializedEntityObject);
            serializedEntity = serializedEntityObject as SerializedEntity;
            if(serializedEntity != null)
            {
                this.safeRepository.CreateOperationDestinationSystemEntity(
                    new OperationSerializedEntity(
                        this.operationExecutive.CurrentOperation,
                        serializedEntity));
            }
        }

        private SerializedEntity SerializeEntity(TEntity entity)
        {
            Persistence.ISerializedEntity serializedEntity =
                this.hashingSerializer.Serialize(entity);
            return new SerializedEntity(
                serializedEntity.DataHash,
                serializedEntity.Data,
                serializedEntity.Label);
        }

        private void RecordMessages(
            IOperationCompletedArgs args,
            EntityIdentifier sourceSystemEntityId = null)
        {
            var messages = new HashSet<Message>();
            this.AddLogMessages(args, messages);
            this.AddException(args, messages);
            this.RecordMessageEntries(messages, args, sourceSystemEntityId);
        }

        private void AddLogMessages(
            IOperationCompletedArgs args, HashSet<Message> messages)
        {
            foreach(ILogMessage logMessage in args.LogMessages)
            {
                this.AddMessage(messages, logMessage.Type, logMessage.Message);
            }
        }

        private void AddException(
            IOperationCompletedArgs args, HashSet<Message> messages)
        {
            if(args.Exception != null)
            {
                this.AddMessage(messages, args.Exception);
            }
        }

        private void AddMessage(HashSet<Message> messages, Exception exception)
        {
            this.AddMessage(
                messages, MessageType.Exception, exception.ToString());
        }

        private void AddMessage(
            HashSet<Message> messages, MessageType type, string text)
        {
            string textHash = this.hashFunction.ComputeHash(text);
            messages.Add(new Message((int) type, textHash, text));
        }

        private void RecordMessageEntries(
            IEnumerable<Message> messages,
            IOperationCompletedArgs args,
            EntityIdentifier sourceSystemEntityId)
        {
            foreach(Message message in messages)
            {
                if(sourceSystemEntityId != null)
                {
                    var identityOperationMessage = new IdentityOperationMessage(
                        this.operationExecutive.CurrentOperation,
                        this.context.EntityType.Id,
                        this.context.SourceSystem.Id,
                        sourceSystemEntityId,
                        message);
                    this.safeRepository
                        .CreateIdentityOperationMessage(identityOperationMessage);
                }
                else
                {
                    var operationMessage = new OperationMessage(
                        this.operationExecutive.CurrentOperation,
                        message);
                    this.safeRepository.CreateOperationMessage(operationMessage);
                }
            }
        }

        private abstract class Operation : IOperation
        {
            private readonly IOperation operation;

            public Operation(IOperation operation)
            {
                this.operation = operation;
            }

            public Guid Id
            {
                get { return this.operation.Id; }
            }

            public DateTimeOffset TimeStamp
            {
                get { return this.operation.TimeStamp; }
            }
        }

        private abstract class EntityTransaction : Operation, IEntityTransaction
        {
            protected EntityTransaction(IOperation operation, int outcome)
                : base(operation)
            {
                this.Outcome = outcome;
            }

            public int Outcome { get; private set; }
        }

        private class MappingEntityTransaction :
            EntityTransaction, IMappingEntityTransaction
        {
            public MappingEntityTransaction(
                IOperation operation, int outcome, Guid mappingId)
                : base(operation, outcome)
            {
                this.MappingId = mappingId;
            }

            public Guid MappingId { get; private set; }
        }

        private class IdentityEntityTransaction :
            EntityTransaction, IIdentityEntityTransaction
        {
            public IdentityEntityTransaction(
                IOperation operation,
                int outcome,
                Guid entityTypeId,
                Guid sourceSystemId,
                string sourceSystemEntityId)
                : base(operation, outcome)
            {
                this.Identity = new SourceSystemEntityIdentity(
                    entityTypeId, sourceSystemId, sourceSystemEntityId);
            }

            public ISourceSystemEntityIdentity Identity { get; private set; }
        }

        private class SerializedEntity : Persistence.ISerializedEntity
        {
            public SerializedEntity(string dataHash, byte[] data, string label)
            {
                this.DataHash = dataHash;
                this.Data = data;
                this.Label = label;
            }

            public string DataHash { get; private set; }

            public byte[] Data { get; private set; }

            public string Label { get; private set; }
        }

        private class OperationSerializedEntity : IOperationSerializedEntity
        {
            public OperationSerializedEntity(
                IOperation operation, SerializedEntity entity)
            {
                this.Operation = operation;
                this.Entity = entity;
            }

            public IOperation Operation { get; private set; }

            public Persistence.ISerializedEntity Entity { get; private set; }
        }

        private class PersistenceValueChange : Persistence.IValueChange
        {
            public PersistenceValueChange(
                Guid id,
                Guid mappingEntityTransactionId,
                string valueName,
                string previousValue,
                string newValue)
            {
                this.Id = id;
                this.MappingEntityTransactionId = mappingEntityTransactionId;
                this.ValueName = valueName;
                this.PreviousValue = previousValue;
                this.NewValue = newValue;
            }

            public Guid Id { get; private set; }

            public Guid MappingEntityTransactionId { get; private set; }

            public string ValueName { get; private set; }

            public string PreviousValue { get; private set; }

            public string NewValue { get; private set; }
        }

        private class IdentityOperationSourceSystemEntity :
            IIdentityOperationSourceSystemEntity
        {
            public IdentityOperationSourceSystemEntity(
                IOperation operation,
                Guid entityTypeId,
                Guid sourceSystemId,
                string sourceSystemEntityId,
                SerializedEntity entity)
            {
                this.Identity = new SourceSystemEntityIdentity(
                    entityTypeId, sourceSystemId, sourceSystemEntityId);
                this.Operation = operation;
                this.Entity = entity;
            }

            public ISourceSystemEntityIdentity Identity { get; private set; }

            public IOperation Operation { get; private set; }

            public Persistence.ISerializedEntity Entity { get; private set; }
        }

        private class EntityDeletion : Operation, IEntityDeletion
        {
            public EntityDeletion(
                IOperation operation,
                Guid entityTypeId,
                string destinationSystemEntityId,
                int outcome)
                : base(operation)
            {
                this.EntityTypeId = entityTypeId;
                this.DestinationSystemEntityId = destinationSystemEntityId;
                this.Outcome = outcome;
            }

            public Guid EntityTypeId { get; private set; }

            public string DestinationSystemEntityId { get; private set; }

            public int Outcome { get; private set; }
        }

        private class Message : IMessage
        {
            public Message(int type, string textHash, string text)
            {
                this.Type = type;
                this.TextHash = textHash;
                this.Text = text;
            }

            public int Type { get; private set; }

            public string TextHash { get; private set; }

            public string Text { get; private set; }

            public override int GetHashCode()
            {
                return $"{this.Type}_{this.TextHash}_{this.Text}".GetHashCode();
            }

            public override bool Equals(object obj)
            {
                var other = (Message) obj;
                return
                    this.Type == other.Type &&
                    this.TextHash == other.TextHash &&
                    this.Text == other.Text;
            }
        }

        private class IdentityOperationMessage : IIdentityOperationMessage
        {
            public IdentityOperationMessage(
                IOperation operation,
                Guid entityTypeId,
                Guid sourceSystemId,
                string sourceSystemEntityId,
                Message message)
            {
                this.Identity = new SourceSystemEntityIdentity(
                    entityTypeId, sourceSystemId, sourceSystemEntityId);
                this.Operation = operation;
                this.Message = message;
            }

            public ISourceSystemEntityIdentity Identity { get; private set; }

            public IOperation Operation { get; private set; }

            public IMessage Message { get; private set; }
        }

        private class OperationMessage : IOperationMessage
        {
            public OperationMessage(IOperation operation, IMessage message)
            {
                this.Operation = operation;
                this.Message = message;
            }

            public IOperation Operation { get; private set; }

            public IMessage Message { get; private set; }
        }
    }
}
