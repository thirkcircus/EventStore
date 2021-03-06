// Copyright (c) 2012, Event Store LLP
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
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Tcp;

namespace EventStore.TestClient.Commands
{
    internal class TransactionWriteProcessor : ICmdProcessor
    {
        public string Usage { get { return "TWR [<stream-id> [<expected-version> [<events-cnt>]]]"; } }
        public string Keyword { get { return "TWR"; } }

        public bool Execute(CommandProcessorContext context, string[] args)
        {
            var eventStreamId = "test-stream";
            var expectedVersion = ExpectedVersion.Any;
            int eventsCnt = 10;

            if (args.Length > 0)
            {
                if (args.Length > 3)
                    return false;
                eventStreamId = args[0];
                if (args.Length > 1)
                    expectedVersion = args[1].ToUpper() == "ANY" ? ExpectedVersion.Any : int.Parse(args[1]);
                if (args.Length > 2)
                    eventsCnt = int.Parse(args[1]);
            }

            context.IsAsync();

            var sw = new Stopwatch();
            var stage = Stage.AcquiringTransactionId;
            long transactionId = -1;
            var writtenEvents = 0;
            var corrid = Guid.NewGuid();
            context.Client.CreateTcpConnection(
                    context,
                    connectionEstablished: conn =>
                    {
                        context.Log.Info("[{0}]: Starting transaction...", conn.EffectiveEndPoint);
                        sw.Start();
                        
                        var tranStart = new ClientMessageDto.TransactionStart(corrid, eventStreamId, expectedVersion);
                        var package = new TcpPackage(TcpCommand.TransactionStart, corrid, tranStart.Serialize());
                        conn.EnqueueSend(package.AsByteArray());
                    },
                    handlePackage: (conn, pkg) =>
                    {
                        switch (stage)
                        {
                            case Stage.AcquiringTransactionId:
                            {
                                if (pkg.Command != TcpCommand.TransactionStartCompleted)
                                {
                                    context.Fail(reason: string.Format("Unexpected TCP package: {0}.", pkg.Command));
                                    return;
                                }

                                var dto = pkg.Data.Deserialize<ClientMessageDto.TransactionStartCompleted>();
                                if ((OperationErrorCode)dto.ErrorCode != OperationErrorCode.Success)
                                {
                                    var msg = string.Format("Error while starting transaction: {0} ({1}).", dto.Error, (OperationErrorCode)dto.ErrorCode);
                                    context.Log.Info(msg);
                                    context.Fail(reason: msg);
                                }
                                else
                                {
                                    context.Log.Info("Successfully started transaction. TransactionId: {0}.", dto.TransactionId);
                                    context.Log.Info("Now sending transactional events...", dto.TransactionId);

                                    transactionId = dto.TransactionId;
                                    stage = Stage.Writing;
                                    for (int i = 0; i < eventsCnt; ++i)
                                    {
                                        var writeDto = new ClientMessageDto.TransactionWrite(
                                                corrid,
                                                transactionId,
                                                eventStreamId,
                                                new[] 
                                                { 
                                                        new ClientMessageDto.Event(Guid.NewGuid() ,
                                                                                   "TakeSomeSpaceEvent",
                                                                                   Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()),
                                                                                   Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()))
                                                });
                                        var package = new TcpPackage(TcpCommand.TransactionWrite, corrid, writeDto.Serialize());
                                        conn.EnqueueSend(package.AsByteArray());
                                    }
                                }

                                break;
                            }
                            case Stage.Writing:
                            {
                                if (pkg.Command != TcpCommand.TransactionWriteCompleted)
                                {
                                    context.Fail(reason: string.Format("Unexpected TCP package: {0}.", pkg.Command));
                                    return;
                                }

                                var dto = pkg.Data.Deserialize<ClientMessageDto.TransactionWriteCompleted>();
                                if ((OperationErrorCode)dto.ErrorCode != OperationErrorCode.Success)
                                {
                                    var msg = string.Format("Error while writing transactional event: {0} ({1}).", dto.Error, (OperationErrorCode)dto.ErrorCode);
                                    context.Log.Info(msg);
                                    context.Fail(reason: msg);
                                }
                                else
                                {
                                    writtenEvents += 1;
                                    if (writtenEvents == eventsCnt)
                                    {
                                        context.Log.Info("Written all events. Committing...");

                                        stage = Stage.Committing;
                                        var commitDto = new ClientMessageDto.TransactionCommit(corrid, transactionId, eventStreamId);
                                        var package = new TcpPackage(TcpCommand.TransactionCommit, corrid, commitDto.Serialize());
                                        conn.EnqueueSend(package.AsByteArray());
                                    }
                                }
                                break;
                            }
                            case Stage.Committing:
                            {
                                if (pkg.Command != TcpCommand.TransactionCommitCompleted)
                                {
                                    context.Fail(reason: string.Format("Unexpected TCP package: {0}.", pkg.Command));
                                    return;
                                }

                                sw.Stop();

                                var dto = pkg.Data.Deserialize<ClientMessageDto.TransactionCommitCompleted>();
                                if ((OperationErrorCode)dto.ErrorCode != OperationErrorCode.Success)
                                {
                                    var msg = string.Format("Error while committing transaction: {0} ({1}).", dto.Error, (OperationErrorCode)dto.ErrorCode);
                                    context.Log.Info(msg);
                                    context.Log.Info("Transaction took: {0}.", sw.Elapsed);
                                    context.Fail(reason: msg);
                                }
                                else
                                {
                                    context.Log.Info("Successfully committed transaction [{0}]!", dto.TransactionId);
                                    context.Log.Info("Transaction took: {0}.", sw.Elapsed);
                                    PerfUtils.LogTeamCityGraphData(string.Format("{0}-latency-ms", Keyword), (int)sw.ElapsedMilliseconds);
                                    context.Success();
                                }
                                conn.Close();
                                break;
                            }
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                    },
                    connectionClosed: (connection, error) =>
                    {
                        if (error == SocketError.Success && stage == Stage.Done)
                            context.Success();
                        else
                            context.Fail();
                    });

            context.WaitForCompletion();
            return true;
        }

        private enum Stage
        {
            AcquiringTransactionId,
            Writing,
            Committing,
            Done
        }
    }
}