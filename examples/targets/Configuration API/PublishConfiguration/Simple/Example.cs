using System;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace NLog.Example
{
    /// <summary>
    /// Demonstrates the explicit configuration publish receipt (<see cref="LogFactory.PublishConfiguration"/>)
    /// with two targets. The new configuration is only activated after its targets have been initialized
    /// successfully; old in-flight events continue to the old target set and are not mixed with the new
    /// configuration.
    /// </summary>
    class Example
    {
        static void Main(string[] args)
        {
            // Create a local LogFactory (does not affect the global LogManager).
            var logFactory = new LogFactory();

            // ---- First configuration: two targets (Console + Debug) ----
            var consoleTarget = new ConsoleTarget { Name = "console" };
            consoleTarget.Layout = "  ";

            var debugTarget = new DebugTarget { Name = "debug" };
            debugTarget.Layout = " ";

            var firstConfig = new LoggingConfiguration();
            firstConfig.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget);
            firstConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, debugTarget);

            // Publish the first configuration and inspect the receipt.
            var firstResult = logFactory.PublishConfiguration(firstConfig);
            Console.WriteLine($"First publish succeeded: {firstResult.Succeeded}");

            var logger = logFactory.GetLogger("Example");
            logger.Debug("This goes to Debug target only.");
            logger.Info("This goes to both Console and Debug targets.");

            // ---- Second configuration: replace with two new targets ----
            var newConsoleTarget = new ConsoleTarget { Name = "console2" };
            newConsoleTarget.Layout = "[NEW]  ";

            var newDebugTarget = new DebugTarget { Name = "debug2" };
            newDebugTarget.Layout = "[NEW]  ";

            var secondConfig = new LoggingConfiguration();
            secondConfig.AddRule(LogLevel.Info, LogLevel.Fatal, newConsoleTarget);
            secondConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, newDebugTarget);

            // Publish the second configuration. The receipt reports whether activation occurred.
            var secondResult = logFactory.PublishConfiguration(secondConfig);
            Console.WriteLine($"Second publish succeeded: {secondResult.Succeeded}");
            Console.WriteLine($"Previous config was replaced: {secondResult.PreviousConfiguration != null}");

            // New events go to the new target set. Old in-flight events (if any) continue to the old set.
            logger.Info("This goes to the new targets.");

            // Flush binds to the currently active configuration and does not wait for future events.
            logFactory.Flush();
            Console.WriteLine($"Debug target received {debugTarget.Counter} events (old config).");
            Console.WriteLine($"New Debug target received {newDebugTarget.Counter} events (new config).");

            // ---- Failed publish: new target that cannot initialize ----
            var badTarget = new BadTarget { Name = "bad" };
            var badConfig = new LoggingConfiguration();
            badConfig.AddRuleForAllLevels(badTarget);

            var failedResult = logFactory.PublishConfiguration(badConfig);
            Console.WriteLine($"Failed publish succeeded: {failedResult.Succeeded}");
            Console.WriteLine($"Active config unchanged: {ReferenceEquals(logFactory.Configuration, secondConfig)}");

            // Clean up.
            logFactory.Shutdown();
            Console.WriteLine("Done.");
        }
    }

    /// <summary>
    /// A target that always fails to initialize, used to demonstrate that a failed publish
    /// leaves the active configuration unchanged and only cleans up the new targets.
    /// </summary>
    public class BadTarget : Target
    {
        protected override void InitializeTarget()
        {
            throw new InvalidOperationException("Simulated initialization failure.");
        }
    }
}
