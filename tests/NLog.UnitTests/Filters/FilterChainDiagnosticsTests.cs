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

namespace NLog.UnitTests.Filters
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using NLog.Common;
    using NLog.Config;
    using NLog.Filters;
    using NLog.Targets;
    using Xunit;

    public class FilterChainDiagnosticsTests : NLogTestBase
    {
        private static WhenMethodFilter CreateFilter(FilterResult result) => new WhenMethodFilter(_ => result);

        private static LoggingRule CreateThreeFilterRule(LogFactory logFactory, MemoryTarget target, Filter filter1, Filter filter2, Filter filter3, out List<KeyValuePair<Filter, FilterResult>> reports)
        {
            var observed = new List<KeyValuePair<Filter, FilterResult>>();
            logFactory.FilterChainDiagnostics = (filter, logEvent, result) => observed.Add(new KeyValuePair<Filter, FilterResult>(filter, result));

            var config = new LoggingConfiguration(logFactory);
            config.AddRuleForAllLevels(target);
            var rule = config.LoggingRules[config.LoggingRules.Count - 1];
            rule.Filters.Add(filter1);
            rule.Filters.Add(filter2);
            rule.Filters.Add(filter3);
            logFactory.Configuration = config;

            reports = observed;
            return rule;
        }

        [Fact]
        public void IgnoreFinalFilter_ReportsExecutedFilters_AndShortCircuits()
        {
            // Stage
            var logFactory = new LogFactory();
            var target = new MemoryTarget("ignoreFinalTarget") { Layout = "${message}" };
            var filter1 = CreateFilter(FilterResult.Neutral);
            var filter2 = CreateFilter(FilterResult.IgnoreFinal);
            var filter3 = CreateFilter(FilterResult.Log);
            CreateThreeFilterRule(logFactory, target, filter1, filter2, filter3, out var reports);

            // Act
            logFactory.GetLogger("IgnoreFinal").Info("ignored message");

            // Assert - only the actually executed filters are reported, filter3 never ran (short-circuit)
            Assert.Equal(2, reports.Count);
            Assert.Same(filter1, reports[0].Key);
            Assert.Equal(FilterResult.Neutral, reports[0].Value);
            Assert.Same(filter2, reports[1].Key);
            Assert.Equal(FilterResult.IgnoreFinal, reports[1].Value);
            Assert.DoesNotContain(reports, r => ReferenceEquals(r.Key, filter3));
            Assert.Empty(target.Logs);
        }

        [Fact]
        public void LogFinalFilter_ReportsExecutedFilters_AndShortCircuits()
        {
            // Stage
            var logFactory = new LogFactory();
            var target1 = new MemoryTarget("logFinalTarget1") { Layout = "${message}" };
            var target2 = new MemoryTarget("logFinalTarget2") { Layout = "${message}" };
            var filter1 = CreateFilter(FilterResult.Neutral);
            var filter2 = CreateFilter(FilterResult.LogFinal);
            var filter3 = CreateFilter(FilterResult.Ignore);
            var rule = CreateThreeFilterRule(logFactory, target1, filter1, filter2, filter3, out var reports);
            rule.Targets.Add(target2);
            logFactory.ReconfigExistingLoggers();

            // Act
            logFactory.GetLogger("LogFinal").Info("final message");

            // Assert - filter3 never ran, LogFinal wrote to first target and stopped the target-chain
            Assert.Equal(2, reports.Count);
            Assert.Same(filter1, reports[0].Key);
            Assert.Equal(FilterResult.Neutral, reports[0].Value);
            Assert.Same(filter2, reports[1].Key);
            Assert.Equal(FilterResult.LogFinal, reports[1].Value);
            Assert.DoesNotContain(reports, r => ReferenceEquals(r.Key, filter3));
            Assert.Equal(new[] { "final message" }, target1.Logs);
            Assert.Empty(target2.Logs);
        }

        [Fact]
        public void NeutralFilters_ThrowingDiagnostics_KeepsOriginalConclusion()
        {
            // Stage
            var logFactory = new LogFactory();
            var target = new MemoryTarget("neutralTarget") { Layout = "${message}" };
            var filter1 = CreateFilter(FilterResult.Neutral);
            var filter2 = CreateFilter(FilterResult.Neutral);
            var filter3 = CreateFilter(FilterResult.Neutral);
            var rule = CreateThreeFilterRule(logFactory, target, filter1, filter2, filter3, out _);
            rule.FilterDefaultAction = FilterResult.Log;
            logFactory.ReconfigExistingLoggers();

            var internalLogOutput = new StringWriter();
            InternalLogger.LogLevel = LogLevel.Warn;
            InternalLogger.LogWriter = internalLogOutput;

            int callbackCount = 0;
            logFactory.FilterChainDiagnostics = (filter, logEvent, result) =>
            {
                ++callbackCount;
                throw new InvalidOperationException("Diagnostics failure must not change the conclusion.");
            };

            // Act
            logFactory.GetLogger("Neutral").Info("neutral message");

            // Assert - all three filters executed and were reported, and the original conclusion (FilterDefaultAction.Log) survived
            Assert.Equal(3, callbackCount);
            Assert.Equal(new[] { "neutral message" }, target.Logs);

            // Assert - diagnostics failure went to the InternalLogger, and was not recursively written to any logger-target
            var internalLog = internalLogOutput.ToString();
            Assert.Contains("FilterChainDiagnostics", internalLog);
            Assert.Equal(new[] { "neutral message" }, target.Logs);
        }

        [Fact]
        public void FilterGetFilterResult_RegressionExample_AlignedWithDiagnostics()
        {
            // Regression-example for the original Filter.GetFilterResult-interface.
            // Direct calls must produce the same results as reported by FilterChainDiagnostics.
            var filter1 = CreateFilter(FilterResult.Neutral);
            var filter2 = CreateFilter(FilterResult.IgnoreFinal);
            var filter3 = CreateFilter(FilterResult.Log);
            var logEvent = LogEventInfo.Create(LogLevel.Info, "Regression", "message");

            var directResults = new List<FilterResult>
            {
                filter1.GetFilterResult(logEvent),
                filter2.GetFilterResult(logEvent),
                filter3.GetFilterResult(logEvent),
            };
            Assert.Equal(new[] { FilterResult.Neutral, FilterResult.IgnoreFinal, FilterResult.Log }, directResults);

            // Same filters through the logging pipeline with diagnostics
            var logFactory = new LogFactory();
            var target = new MemoryTarget("regressionTarget") { Layout = "${message}" };
            CreateThreeFilterRule(logFactory, target, filter1, filter2, filter3, out var reports);
            logFactory.GetLogger("Regression").Info("message");

            // The reported results align with the direct GetFilterResult-results for the executed filters,
            // and the not-executed filter3 is not reported with a fake result.
            Assert.Equal(2, reports.Count);
            Assert.Equal(directResults[0], reports[0].Value);
            Assert.Equal(directResults[1], reports[1].Value);
            Assert.DoesNotContain(reports, r => r.Value == directResults[2]);
            Assert.Empty(target.Logs);
        }
    }
}
