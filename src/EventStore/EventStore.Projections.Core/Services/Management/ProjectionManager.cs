﻿// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EventStore.Common.Log;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Standard;

namespace EventStore.Projections.Core.Services.Management
{
    public class ProjectionManager : IDisposable,
                                     IHandle<SystemMessage.SystemInit>,
                                     IHandle<SystemMessage.SystemStart>,
                                     IHandle<ClientMessage.ReadEventsBackwardsCompleted>,
                                     IHandle<ClientMessage.WriteEventsCompleted>,
                                     IHandle<ProjectionManagementMessage.Post>,
                                     IHandle<ProjectionManagementMessage.UpdateQuery>,
                                     IHandle<ProjectionManagementMessage.GetQuery>,
                                     IHandle<ProjectionManagementMessage.Delete>,
                                     IHandle<ProjectionManagementMessage.GetStatistics>,
                                     IHandle<ProjectionManagementMessage.GetState>,
                                     IHandle<ProjectionManagementMessage.Disable>,
                                     IHandle<ProjectionManagementMessage.Enable>,
                                     IHandle<ProjectionMessage.Projections.Started>,
                                     IHandle<ProjectionMessage.Projections.Stopped>,
                                     IHandle<ProjectionMessage.Projections.Faulted>
    {
        private readonly ILogger _logger = LogManager.GetLoggerFor<ProjectionManager>();

        private readonly IPublisher _coreOutput;
        private readonly ICheckpoint _checkpointForStatistics;
        private readonly ProjectionStateHandlerFactory _projectionStateHandlerFactory;
        private readonly Dictionary<string, ManagedProjection> _projections;
        private readonly Dictionary<Guid, string> _projectionsMap;

        private readonly RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted>
            _writeDispatcher;

        private readonly
            RequestResponseDispatcher<ClientMessage.ReadEventsBackwards, ClientMessage.ReadEventsBackwardsCompleted>
            _readDispatcher;

        private int _readEventsBatchSize = 100;

        public ProjectionManager(IPublisher coreOutput, ICheckpoint checkpointForStatistics)
        {
            if (coreOutput == null) throw new ArgumentNullException("coreOutput");

            _writeDispatcher =
                new RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted>(
                    coreOutput, v => v.CorrelationId, v => v.CorrelationId);
            _readDispatcher =
                new RequestResponseDispatcher
                    <ClientMessage.ReadEventsBackwards, ClientMessage.ReadEventsBackwardsCompleted>(
                    coreOutput, v => v.CorrelationId, v => v.CorrelationId);

            _coreOutput = coreOutput;
            _checkpointForStatistics = checkpointForStatistics;

            _projectionStateHandlerFactory = new ProjectionStateHandlerFactory();
            _projections = new Dictionary<string, ManagedProjection>();
            _projectionsMap = new Dictionary<Guid, string>();
        }

        public void Handle(SystemMessage.SystemInit message)
        {
        }

        public void Handle(SystemMessage.SystemStart message)
        {
            _coreOutput.Publish(new ProjectionMessage.CoreService.Start());
            StartExistingProjections();
        }

        public void Handle(ProjectionManagementMessage.Post message)
        {
            if (message.Name == null)
            {
                message.Envelope.ReplyWith(
                    new ProjectionManagementMessage.OperationFailed("Projection name is required"));
            }
            else
            {
                if (_projections.ContainsKey(message.Name))
                {
                    message.Envelope.ReplyWith(
                        new ProjectionManagementMessage.OperationFailed("Duplicate projection name: " + message.Name));
                }
                else
                {
                    PostNewProjection(
                        message,
                        managedProjection =>
                        message.Envelope.ReplyWith(new ProjectionManagementMessage.Updated(message.Name)));
                }
            }
        }

        public void Handle(ProjectionManagementMessage.Delete message)
        {
            throw new NotImplementedException();
        }

        public void Handle(ProjectionManagementMessage.GetQuery message)
        {
            var projection = GetProjection(message.Name);
            if (projection == null)
                message.Envelope.ReplyWith(new ProjectionManagementMessage.NotFound());
            else
                projection.Handle(message);
        }

        public void Handle(ProjectionManagementMessage.UpdateQuery message)
        {
            _logger.Info(
                "Updating '{0}' projection source to '{1}' (Requested type is: '{2}')", message.Name, message.Query,
                message.HandlerType);
            var projection = GetProjection(message.Name);
            if (projection == null)
                message.Envelope.ReplyWith(new ProjectionManagementMessage.NotFound());
            else
                projection.Handle(message); // update query text
        }

        public void Handle(ProjectionManagementMessage.Disable message)
        {
            _logger.Info("Disabling '{0}' projection", message.Name);

            var projection = GetProjection(message.Name);
            if (projection == null)
                message.Envelope.ReplyWith(new ProjectionManagementMessage.NotFound());
            else
                projection.Handle(message);
        }

        public void Handle(ProjectionManagementMessage.Enable message)
        {
            _logger.Info("Enabling '{0}' projection", message.Name);

            var projection = GetProjection(message.Name);
            if (projection == null)
                message.Envelope.ReplyWith(new ProjectionManagementMessage.NotFound());
            else
                projection.Handle(message);
        }

        public void Handle(ProjectionManagementMessage.GetStatistics message)
        {
            var transactionFileHeadPosition = _checkpointForStatistics != null ? _checkpointForStatistics.Read() : -1;
            if (!String.IsNullOrEmpty(message.Name))
            {
                var projection = GetProjection(message.Name);
                if (projection == null)
                    message.Envelope.ReplyWith(new ProjectionManagementMessage.NotFound());
                else
                    message.Envelope.ReplyWith(
                        new ProjectionManagementMessage.Statistics(
                            new[] {projection.GetStatistics()}, transactionFileHeadPosition));
            }
            else
            {
                var statuses = (from projectionNameValue in _projections
                                let projection = projectionNameValue.Value
                                where message.Mode == null || message.Mode == projection.GetMode()
                                let status = projection.GetStatistics()
                                select status).ToArray();
                message.Envelope.ReplyWith(
                    new ProjectionManagementMessage.Statistics(statuses, transactionFileHeadPosition));
            }
        }

        public void Handle(ProjectionManagementMessage.GetState message)
        {
            var projection = GetProjection(message.Name);
            if (projection == null)
                message.Envelope.ReplyWith(new ProjectionManagementMessage.NotFound());
            else
                projection.Handle(message);
        }

        public void Handle(ProjectionMessage.Projections.Started message)
        {
            string name;
            if (_projectionsMap.TryGetValue(message.CorrelationId, out name))
            {
                var projection = _projections[name];
                projection.Handle(message);
            }
        }

        public void Handle(ProjectionMessage.Projections.Stopped message)
        {
            string name;
            if (_projectionsMap.TryGetValue(message.CorrelationId, out name))
            {
                var projection = _projections[name];
                projection.Handle(message);
            }
        }

        public void Handle(ProjectionMessage.Projections.Faulted message)
        {
            string name;
            if (_projectionsMap.TryGetValue(message.CorrelationId, out name))
            {
                var projection = _projections[name];
                projection.Handle(message);
            }
        }

        public void Handle(ClientMessage.ReadEventsBackwardsCompleted message)
        {
            _readDispatcher.Handle(message);
        }

        public void Handle(ClientMessage.WriteEventsCompleted message)
        {
            _writeDispatcher.Handle(message);
        }

        public void Dispose()
        {
            foreach (var projection in _projections.Values)
                projection.Dispose();
            _projections.Clear();
        }

        private ManagedProjection GetProjection(string name)
        {
            ManagedProjection result;
            return _projections.TryGetValue(name, out result) ? result : null;
        }

        private void StartExistingProjections()
        {
            BeginLoadProjectionList();
        }

        private void BeginLoadProjectionList(int from = -1)
        {
            _readDispatcher.Publish(
                new ClientMessage.ReadEventsBackwards(
                    Guid.NewGuid(), new PublishEnvelope(_coreOutput), "$projections-$all", from, _readEventsBatchSize,
                    resolveLinks: false), m => LoadProjectionListCompleted(m, from));
        }

        private void LoadProjectionListCompleted(
            ClientMessage.ReadEventsBackwardsCompleted completed, int requestedFrom)
        {
            if (completed.Result == RangeReadResult.Success && completed.Events.Length > 0)
            {
                if (completed.NextEventNumber != -1)
                    BeginLoadProjectionList(@from: completed.NextEventNumber);
                foreach (var @event in completed.Events.Where(v => v.EventType == "ProjectionCreated"))
                {
                    var projectionName = Encoding.UTF8.GetString(@event.Data);
                    if (_projections.ContainsKey(projectionName))
                    {
                        //TODO: log this event as it should not happen
                        continue; // ignore older attempts to create a projection
                    }
                    var managedProjection = CreateManagedProjectionInstance(projectionName);
                    managedProjection.InitializeExisting(projectionName);
                }
            }
            else
            {
                if (requestedFrom == -1)
                {
                    _logger.Info(
                        "Projection manager is initializing from the empty {0} stream", completed.EventStreamId);

                    CreatePredefinedProjections();
                }
            }
        }

        private void CreatePredefinedProjections()
        {
            CreatePredefinedProjection("streams", typeof (IndexStreams), "");
            CreatePredefinedProjection("stream_by_category", typeof(CategorizeStreamByPath), "-");
            CreatePredefinedProjection("by_category", typeof(CategorizeEventsByStreamPath), "-");
        }

        private void CreatePredefinedProjection(string name, Type handlerType, string config)
        {
            IEnvelope envelope = new NoopEnvelope();

            var postMessage = new ProjectionManagementMessage.Post(
                envelope, ProjectionMode.Persistent, name, "native:" + handlerType.Namespace + "." + handlerType.Name,
                config, enabled: false);

            _coreOutput.Publish(postMessage);
        }

        private void PostNewProjection(ProjectionManagementMessage.Post message, Action<ManagedProjection> completed)
        {
            if (message.Mode > ProjectionMode.AdHoc)
            {
                BeginWriteProjectionRegistration(
                    message.Name, () =>
                        {
                            var projection = CreateManagedProjectionInstance(message.Name);
                            projection.InitializeNew(message, () => completed(projection));
                        });
            }
            else
            {
                var projection = CreateManagedProjectionInstance(message.Name);
                projection.InitializeNew(message, () => completed(projection));
            }
        }

        private ManagedProjection CreateManagedProjectionInstance(string name)
        {
            var projectionCorrelationId = Guid.NewGuid();
            var managedProjectionInstance = new ManagedProjection(
                projectionCorrelationId, name, _logger, _writeDispatcher, _readDispatcher, _coreOutput,
                _projectionStateHandlerFactory);
            _projectionsMap.Add(projectionCorrelationId, name);
            _projections.Add(name, managedProjectionInstance);
            return managedProjectionInstance;
        }

        private void BeginWriteProjectionRegistration(string name, Action completed)
        {
            _writeDispatcher.Publish(
                new ClientMessage.WriteEvents(
                    Guid.NewGuid(), new PublishEnvelope(_coreOutput), "$projections-$all", ExpectedVersion.Any,
                    new Event(Guid.NewGuid(), "ProjectionCreated", false, Encoding.UTF8.GetBytes(name), new byte[0])),
                m => WriteProjectionRegistrationCompleted(m, completed, name));
        }

        private void WriteProjectionRegistrationCompleted(
            ClientMessage.WriteEventsCompleted message, Action completed, string name)
        {
            if (message.ErrorCode == OperationErrorCode.Success)
            {
                if (completed != null) completed();
                return;
            }
            _logger.Info(
                "Projection '{0}' registration has not been written to {1}. Error: {2}", name, message.EventStreamId,
                Enum.GetName(typeof (OperationErrorCode), message.ErrorCode));
            if (message.ErrorCode == OperationErrorCode.CommitTimeout
                || message.ErrorCode == OperationErrorCode.ForwardTimeout
                || message.ErrorCode == OperationErrorCode.PrepareTimeout
                || message.ErrorCode == OperationErrorCode.WrongExpectedVersion)
            {
                _logger.Info("Retrying write projection registration for {0}", name);
                BeginWriteProjectionRegistration(name, completed);
            }
            else
                throw new NotSupportedException("Unsupported error code received");
        }
    }
}
