/*// Copyright (c) 2012, Event Store LLP
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

using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Infrastructure.Services.Storage
{
    [TestFixture]
    public class when_having_two_intermingled_transactions_read_index_should : ReadIndexTestScenario
    {
        private EventRecord _p1;
        private EventRecord _p2;
        private EventRecord _p3;
        private EventRecord _p4;
        private EventRecord _p5;

        protected override void WriteTestScenario()
        {
            var t1 = WriteTransactionBegin("ES", ExpectedVersion.NoStream);
            var t2 = WriteTransactionBegin("ABC", ExpectedVersion.NoStream);

            _p1 = WriteTransactionEvent(t1.CorrelationId, t1.LogPosition, t1.EventStreamId, 0, "es1", PrepareFlags.Data);
            _p2 = WriteTransactionEvent(t2.CorrelationId, t2.LogPosition, t2.EventStreamId, 0, "abc1", PrepareFlags.Data);
            _p3 = WriteTransactionEvent(t1.CorrelationId, t1.LogPosition, t1.EventStreamId, 1, "es1", PrepareFlags.Data);
            _p4 = WriteTransactionEvent(t2.CorrelationId, t2.LogPosition, t2.EventStreamId, 1, "abc1", PrepareFlags.Data);
            _p5 = WriteTransactionEvent(t1.CorrelationId, t1.LogPosition, t1.EventStreamId, 2, "es1", PrepareFlags.Data);

            WriteTransactionEnd(t2.CorrelationId, t2.TransactionPosition, t2.EventStreamId);
            WriteTransactionEnd(t1.CorrelationId, t1.TransactionPosition, t1.EventStreamId);

            WriteCommit(t2.CorrelationId, t2.TransactionPosition, t2.EventStreamId, _p2.EventNumber);
            WriteCommit(t1.CorrelationId, t1.TransactionPosition, t1.EventStreamId, _p1.EventNumber);
        }

        [Test]
        public void return_correct_last_event_version_for_larger_stream()
        {
            Assert.AreEqual(2, ReadIndex.GetLastStreamEventNumber("ES"));
        }

        [Test]
        public void return_correct_first_record_for_larger_stream()
        {
            EventRecord prepare;
            Assert.AreEqual(SingleReadResult.Success, ReadIndex.TryReadRecord("ES", 0, out prepare));
            Assert.AreEqual(_p1, prepare);
        }

        [Test]
        public void return_correct_second_record_for_larger_stream()
        {
            EventRecord prepare;
            Assert.AreEqual(SingleReadResult.Success, ReadIndex.TryReadRecord("ES", 1, out prepare));
            Assert.AreEqual(_p3, prepare);
        }

        [Test]
        public void return_correct_third_record_for_larger_stream()
        {
            EventRecord prepare;
            Assert.AreEqual(SingleReadResult.Success, ReadIndex.TryReadRecord("ES", 2, out prepare));
            Assert.AreEqual(_p5, prepare);
        }

        [Test]
        public void not_find_record_with_nonexistent_version_for_larger_stream()
        {
            EventRecord prepare;
            Assert.AreEqual(SingleReadResult.NotFound, ReadIndex.TryReadRecord("ES", 3, out prepare));
        }

        [Test]
        public void return_correct_range_on_from_start_range_query_for_larger_stream()
        {
            EventRecord[] records;
            Assert.AreEqual(RangeReadResult.Success, ReadIndex.TryReadEventsForward("ES", 0, 3, out records));
            Assert.AreEqual(3, records.Length);
            Assert.AreEqual(_p1, records[0]);
            Assert.AreEqual(_p3, records[1]);
            Assert.AreEqual(_p5, records[2]);
        }

        [Test]
        public void return_correct_range_on_from_end_range_query_for_larger_stream_with_specific_version()
        {
            EventRecord[] records;
            Assert.AreEqual(RangeReadResult.Success, ReadIndex.TryReadRecordsBackwards("ES", 2, 3, out records));
            Assert.AreEqual(3, records.Length);
            Assert.AreEqual(_p5, records[0]);
            Assert.AreEqual(_p3, records[1]);
            Assert.AreEqual(_p1, records[2]);
        }

        [Test]
        public void return_correct_range_on_from_end_range_query_for_larger_stream_with_from_end_version()
        {
            EventRecord[] records;
            Assert.AreEqual(RangeReadResult.Success, ReadIndex.TryReadRecordsBackwards("ES", -1, 3, out records));
            Assert.AreEqual(3, records.Length);
            Assert.AreEqual(_p5, records[0]);
            Assert.AreEqual(_p3, records[1]);
            Assert.AreEqual(_p1, records[2]);
        }

        [Test]
        public void return_correct_last_event_version_for_smaller_stream()
        {
            Assert.AreEqual(1, ReadIndex.GetLastStreamEventNumber("ABC"));
        }

        [Test]
        public void return_correct_first_record_for_smaller_stream()
        {
            EventRecord prepare;
            Assert.AreEqual(SingleReadResult.Success, ReadIndex.TryReadRecord("ABC", 0, out prepare));
            Assert.AreEqual(_p2, prepare);
        }

        [Test]
        public void return_correct_second_record_for_smaller_stream()
        {
            EventRecord prepare;
            Assert.AreEqual(SingleReadResult.Success, ReadIndex.TryReadRecord("ABC", 1, out prepare));
            Assert.AreEqual(_p4, prepare);
        }

        [Test]
        public void not_find_record_with_nonexistent_version_for_smaller_stream()
        {
            EventRecord prepare;
            Assert.AreEqual(SingleReadResult.NotFound, ReadIndex.TryReadRecord("ABC", 2, out prepare));
        }

        [Test]
        public void return_correct_range_on_from_start_range_query_for_smaller_stream()
        {
            EventRecord[] records;
            Assert.AreEqual(RangeReadResult.Success, ReadIndex.TryReadEventsForward("ABC", 0, 2, out records));
            Assert.AreEqual(2, records.Length);
            Assert.AreEqual(_p2, records[0]);
            Assert.AreEqual(_p4, records[1]);
        }

        [Test]
        public void return_correct_range_on_from_end_range_query_for_smaller_stream_with_specific_version()
        {
            EventRecord[] records;
            Assert.AreEqual(RangeReadResult.Success, ReadIndex.TryReadRecordsBackwards("ABC", 1, 2, out records));
            Assert.AreEqual(2, records.Length);
            Assert.AreEqual(_p4, records[0]);
            Assert.AreEqual(_p2, records[1]);
        }

        [Test]
        public void return_correct_range_on_from_end_range_query_for_smaller_stream_with_from_end_version()
        {
            EventRecord[] records;
            Assert.AreEqual(RangeReadResult.Success, ReadIndex.TryReadRecordsBackwards("ABC", -1, 2, out records));
            Assert.AreEqual(2, records.Length);
            Assert.AreEqual(_p4, records[0]);
            Assert.AreEqual(_p2, records[1]);
        }
    }
}*/