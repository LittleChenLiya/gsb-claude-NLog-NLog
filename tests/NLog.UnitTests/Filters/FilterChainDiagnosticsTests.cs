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

namespace NLog.UnitTests.Filters
{
    using System;
    using System.Collections.Generic;
    using NLog.Config;
    using NLog.Filters;
    using NLog.Internal;
    using NLog.Targets;
    using Xunit;

    public class FilterChainDiagnosticsTests : NLogTestBase
    {
        private const string LoggerName = "FilterChainDiagnosticsLogger";

        private sealed class ScenarioResult
        {
            public List<string> Executed = new List<string>();      // Recorded inside the filters themselves
            public List<string> Observed = new List<string>();      // Recorded by the diagnostics observer
            public List<string> Target1Logs = new List<string>();
            public List<string> Target2Logs = new List<string>();
        }

        private static ScenarioResult RunScenario(FilterResult[] filterResults, Action<Filter, FilterResult, LogEventInfo> observer, FilterResult? filterDefaultAction = null)
        {
            var result = new ScenarioResult();
            var logFactory = new LogFactory();
            try
            {
                var target1 = new MemoryTarget("mem1") { Layout = "${message}" };
                var target2 = new MemoryTarget("mem2") { Layout = "${message}" };

                var config = new LoggingConfiguration(logFactory);
                var rule = new LoggingRule("*", LogLevel.Info, target1);
                rule.Targets.Add(target2);
                if (filterDefaultAction.HasValue)
                    rule.FilterDefaultAction = filterDefaultAction.Value;

                var filters = new List<Filter>();
                for (int i = 0; i < filterResults.Length; ++i)
                {
                    var filterName = "f" + (i + 1);
                    var filterResult = filterResults[i];
                    filters.Add(new WhenMethodFilter(_ =>
                    {
                        result.Executed.Add(filterName);
                        return filterResult;
                    }));
                }
                foreach (var filter in filters)
                    rule.Filters.Add(filter);

                config.LoggingRules.Add(rule);
                logFactory.Configuration = config;

                if (observer != null)
                {
                    FilterChainDiagnostics.FilterResultObserver = (filter, filterResult, logEvent) =>
                    {
                        if (string.Equals(logEvent.LoggerName, LoggerName, StringComparison.Ordinal))
                            result.Observed.Add("f" + (filters.IndexOf(filter) + 1) + ":" + filterResult);
                        observer(filter, filterResult, logEvent);
                    };
                }

                try
                {
                    logFactory.GetLogger(LoggerName).Info("Test Message");
                }
                finally
                {
                    FilterChainDiagnostics.FilterResultObserver = null;
                }

                result.Target1Logs.AddRange(target1.Logs);
                result.Target2Logs.AddRange(target2.Logs);
                return result;
            }
            finally
            {
                logFactory.Shutdown();
            }
        }

        [Fact]
        public void IgnoreFinal_ShortCircuits_AndReportsOnlyExecutedFilters()
        {
            var result = RunScenario(new[] { FilterResult.Neutral, FilterResult.IgnoreFinal, FilterResult.Log }, null);

            // f3 was never executed, and no fake result was reported for it
            Assert.Equal(new[] { "f1", "f2" }, result.Executed);
            Assert.Empty(result.Target1Logs);
            Assert.Empty(result.Target2Logs);   // IgnoreFinal also stops the remaining targets in the chain

            var observed = RunScenario(new[] { FilterResult.Neutral, FilterResult.IgnoreFinal, FilterResult.Log }, (f, r, e) => { });
            Assert.Equal(new[] { "f1:Neutral", "f2:IgnoreFinal" }, observed.Observed);
            Assert.Equal(new[] { "f1", "f2" }, observed.Executed);
            Assert.Empty(observed.Target1Logs);
            Assert.Empty(observed.Target2Logs);
        }

        [Fact]
        public void LogFinal_ShortCircuits_AndReportsOnlyExecutedFilters()
        {
            var result = RunScenario(new[] { FilterResult.Neutral, FilterResult.LogFinal, FilterResult.Ignore }, null);

            // f3 was never executed, and no fake result was reported for it
            Assert.Equal(new[] { "f1", "f2" }, result.Executed);
            Assert.Equal(new[] { "Test Message" }, result.Target1Logs);
            Assert.Empty(result.Target2Logs);   // LogFinal stops the remaining targets in the chain

            var observed = RunScenario(new[] { FilterResult.Neutral, FilterResult.LogFinal, FilterResult.Ignore }, (f, r, e) => { });
            Assert.Equal(new[] { "f1:Neutral", "f2:LogFinal" }, observed.Observed);
            Assert.Equal(new[] { "f1", "f2" }, observed.Executed);
            Assert.Equal(new[] { "Test Message" }, observed.Target1Logs);
            Assert.Empty(observed.Target2Logs);
        }

        [Fact]
        public void Neutral_ExecutesAllFilters_AndFallsBackToDefaultAction()
        {
            var result = RunScenario(new[] { FilterResult.Neutral, FilterResult.Neutral, FilterResult.Neutral }, (f, r, e) => { }, FilterResult.Log);

            Assert.Equal(new[] { "f1", "f2", "f3" }, result.Executed);
            Assert.Equal(new[] { "f1:Neutral", "f2:Neutral", "f3:Neutral" }, result.Observed);
            Assert.Equal(new[] { "Test Message" }, result.Target1Logs);     // No filter decided, so the rule default-action decides
            Assert.Equal(new[] { "Test Message" }, result.Target2Logs);
        }

        [Fact]
        public void ThrowingObserver_DoesNotChange_FilterConclusion()
        {
            var scenarios = new[]
            {
                Tuple.Create(new[] { FilterResult.Neutral, FilterResult.IgnoreFinal, FilterResult.Log }, (FilterResult?)null),
                Tuple.Create(new[] { FilterResult.Neutral, FilterResult.LogFinal, FilterResult.Ignore }, (FilterResult?)null),
                Tuple.Create(new[] { FilterResult.Neutral, FilterResult.Neutral, FilterResult.Neutral }, (FilterResult?)FilterResult.Log),
            };

            foreach (var scenario in scenarios)
            {
                // Regression for the original interface - conclusion without any observer
                var baseline = RunScenario(scenario.Item1, null, scenario.Item2);

                // Observer that always fails must not change the allow/deny conclusion
                var failing = RunScenario(scenario.Item1, (f, r, e) =>
                {
                    if (string.Equals(e.LoggerName, LoggerName, StringComparison.Ordinal))
                        throw new InvalidOperationException("Observer failure must be swallowed");
                }, scenario.Item2);

                Assert.Equal(baseline.Executed, failing.Executed);      // All filters still executed in the same order
                Assert.Equal(baseline.Target1Logs, failing.Target1Logs);
                Assert.Equal(baseline.Target2Logs, failing.Target2Logs);
            }
        }

        [Fact]
        public void RecursiveLoggingObserver_DoesNotRecurse_AndKeepsConclusion()
        {
            var observerFactory = new LogFactory();
            var observerTarget = new MemoryTarget("observerMem") { Layout = "${message}" };
            try
            {
                var observerConfig = new LoggingConfiguration(observerFactory);
                var observerRule = new LoggingRule("*", LogLevel.Info, observerTarget);
                observerRule.FilterDefaultAction = FilterResult.Log;
                observerRule.Filters.Add(new WhenMethodFilter(_ => FilterResult.Neutral));   // Nested chain would report again, if not guarded
                observerConfig.LoggingRules.Add(observerRule);
                observerFactory.Configuration = observerConfig;

                int observerCalls = 0;
                var result = RunScenario(new[] { FilterResult.Neutral, FilterResult.Neutral, FilterResult.Neutral }, (f, r, e) =>
                {
                    ++observerCalls;
                    observerFactory.GetLogger("ObserverLogger").Info("Observer logging");    // Must not recurse into the observer
                }, FilterResult.Log);

                Assert.Equal(3, observerCalls);                         // Exactly one call per executed filter, no recursion
                Assert.Equal(new[] { "f1", "f2", "f3" }, result.Executed);
                Assert.Equal(new[] { "Test Message" }, result.Target1Logs);   // Conclusion unchanged
                Assert.Equal(3, observerTarget.Logs.Count);             // Observer was still able to log
            }
            finally
            {
                observerFactory.Shutdown();
            }
        }
    }
}
