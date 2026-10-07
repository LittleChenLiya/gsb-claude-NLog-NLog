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
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO,
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//

namespace NLog.Internal
{
    using System;
    using NLog.Common;
    using NLog.Filters;

    /// <summary>
    /// Diagnostic sink that observes the actual result of every filter that is really executed
    /// in a filter-chain. Filters that are skipped because of short-circuit (Final-results) are
    /// never reported, so no fake results are exposed for filters that did not run.
    /// </summary>
    internal static class FilterChainDiagnostics
    {
        [ThreadStatic]
        private static bool _isReporting;

        /// <summary>
        /// Gets or sets the observer that is notified after a filter has actually been executed.
        /// Arguments are: the executed filter, the result it returned and the current log event.
        /// </summary>
        internal static Action<Filter, FilterResult, LogEventInfo>? FilterResultObserver { get; set; }

        /// <summary>
        /// Reports the result of one executed filter to the registered observer.
        /// A failing observer never changes the filter-conclusion, and an observer that
        /// performs its own logging does not cause recursive reporting.
        /// </summary>
        internal static void ReportFilterResult(Filter filter, FilterResult filterResult, LogEventInfo logEvent)
        {
            var observer = FilterResultObserver;
            if (observer is null || _isReporting)
                return; // No observer, or observer triggered its own logging - avoid recursion

            _isReporting = true;
            try
            {
                observer(filter, filterResult, logEvent);
            }
            catch (Exception exception)
            {
                // Observer-failure must not change the allow/deny conclusion of the filter-chain.
                // InternalLogger does not route through NLog-loggers, so no recursive logging here.
                InternalLogger.Warn(exception, "FilterChainDiagnostics observer failed. Filter result is not affected.");
            }
            finally
            {
                _isReporting = false;
            }
        }
    }
}
