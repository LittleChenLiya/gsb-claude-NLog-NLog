using System;
using NLog;
using NLog.Config;
using NLog.Targets;

// Two-target example: explicit configuration publish with acknowledgment receipt.
//
// The first configuration writes to two targets (console + memory). While it is
// active, a new configuration is published through LogFactory.PublishConfiguration:
//
//  - The new targets are initialized BEFORE the new configuration is activated.
//  - Logevents that already entered the pipeline keep writing to the old
//    target-collection, and are never mixed into the new configuration.
//  - The old targets are only closed after their in-flight writes have completed.
//  - The returned ConfigPublishReceipt acknowledges the outcome of the publish.
//  - Flush() binds to the target-collection that is active at call-time.
internal static class Program
{
    private static void Main()
    {
        var logFactory = new LogFactory();

        // Configuration 1: two targets - console + memory
        var consoleTarget = new ConsoleTarget("console");
        var memoryTarget = new MemoryTarget("memory") { MaxLogsCount = 100 };
        var config1 = new LoggingConfiguration(logFactory);
        config1.AddRuleForAllLevels(consoleTarget);
        config1.AddRuleForAllLevels(memoryTarget);
        logFactory.Configuration = config1; // Default reconfig API stays supported

        var logger = logFactory.GetLogger("Example");
        logger.Info("Written to the first target-collection");

        // Configuration 2: two new targets - file + memory
        var fileTarget = new FileTarget("file") { FileName = "${basedir}/publish-example.log" };
        var memoryTarget2 = new MemoryTarget("memory2") { MaxLogsCount = 100 };
        var config2 = new LoggingConfiguration(logFactory);
        config2.AddRuleForAllLevels(fileTarget);
        config2.AddRuleForAllLevels(memoryTarget2);

        // Explicit publish with acknowledgment receipt
        ConfigPublishReceipt receipt = logFactory.PublishConfiguration(config2);
        if (!receipt.Succeeded)
        {
            // Initialization failed: only the targets from this attempt were
            // recycled, the previous configuration is still active.
            Console.WriteLine("Publish rejected: {0}", receipt.Error);
            return;
        }

        logger.Info("Written to the second target-collection");

        // Flush binds to the target-collection that is active at call-time,
        // and does not wait for future logevents.
        logFactory.Flush(TimeSpan.FromSeconds(5));

        // Wait until the previous configuration closed its targets (happens once,
        // after all its in-flight writes have completed).
        if (receipt.WaitForPreviousConfigurationClosed(TimeSpan.FromSeconds(15)))
            Console.WriteLine("Previous configuration closed: {0}", receipt.PreviousConfiguration);

        logFactory.Shutdown();
    }
}
