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
using System.Collections.Generic;
using System.IO;
using EventStore.Common.Utils;

namespace EventStore.Core.TransactionLog
{
    public class PrefixFileNamingStrategy : IFileNamingStrategy 
    {
        private readonly string _path;
        private readonly string _prefix;

        public PrefixFileNamingStrategy(string path, string prefix)
        {
            Ensure.NotNull(path, "path");
            Ensure.NotNull(prefix, "prefix");

            _path = path;
            _prefix = prefix;
        }

        public string GetFilenameFor(int index, int version = 0)
        {
            Ensure.Nonnegative(index, "index");
            Ensure.Nonnegative(version, "version");

            return Path.Combine(_path, _prefix + index);
        }

        public string[] GetAllVersionsFor(int index)
        {
            return Directory.GetFiles(_path, _prefix + index);
        }

        public string[] GetAllPresentFiles()
        {
            return Directory.GetFiles(_path, _prefix + "*");
        }
    }
}