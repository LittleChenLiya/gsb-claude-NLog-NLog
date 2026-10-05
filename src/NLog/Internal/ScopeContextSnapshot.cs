//
// Copyright (c) 2004-2024 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
//
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
// * Redistributions of source code must retain the above copyright notice,
//   this list of conditions and the following disclaimer.
//
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
// * Neither the name of Jaroslaw Kowalski nor the names of its
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//

namespace NLog
{
    using System.Collections.Generic;

    /// <summary>
    /// Explicit snapshot of the thread identity and <see cref="ScopeContext"/> properties
    /// captured when a <see cref="LogEventInfo"/> is enqueued for asynchronous processing.
    ///
    /// The snapshot seals the logging context into the log event, so that late layout
    /// evaluation on another thread uses the enqueued context instead of the current
    /// thread's context. Scope property values are stored as-is (no ToString on unknown
    /// objects), leaving the actual layout rendering to the layout renderer.
    /// </summary>
    public sealed class ScopeContextSnapshot
    {
        private readonly Dictionary<string, object?>? _properties;

        /// <summary>
        /// Initializes a new instance of the <see cref="ScopeContextSnapshot"/> class.
        /// </summary>
        /// <param name="managedThreadId">The managed thread id of the logging thread.</param>
        /// <param name="threadName">The name of the logging thread.</param>
        /// <param name="properties">The scope context properties captured at enqueue time.</param>
        public ScopeContextSnapshot(int managedThreadId, string? threadName, IList<KeyValuePair<string, object?>> properties)
        {
            ManagedThreadId = managedThreadId;
            ThreadName = threadName;

            if (properties.Count > 0)
            {
                var dictionary = new Dictionary<string, object?>(properties.Count, ScopeContext.DefaultComparer);
                foreach (var item in properties)
                {
                    dictionary[item.Key] = item.Value;
                }
                _properties = dictionary;
            }
        }

        /// <summary>
        /// Gets the managed thread id of the logging thread.
        /// </summary>
        public int ManagedThreadId { get; }

        /// <summary>
        /// Gets the name of the logging thread.
        /// </summary>
        public string? ThreadName { get; }

        /// <summary>
        /// Attempts to retrieve a scope property value from the snapshot.
        /// </summary>
        /// <param name="key">The scope property key.</param>
        /// <param name="value">The scope property value, or <see langword="null"/> if not found.</param>
        /// <returns><see langword="true"/> if the key was found; otherwise, <see langword="false"/>.</returns>
        public bool TryGetValue(string key, out object? value)
        {
            if (_properties != null)
            {
                return _properties.TryGetValue(key, out value);
            }

            value = null;
            return false;
        }
    }
}
