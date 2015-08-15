using System;
using System.Collections.Generic;

namespace ReTarget.console
{
    class Program
    {
        public static Dictionary<string, List<string>> NuGetPackages = new Dictionary<string, List<string>>();
        static void Main(string[] args)
        {
            string solutionPath, solutionName;
            ConsoleLogger logger = new ConsoleLogger();

            if (!PrintDisclaimer())
            {
                return;
            }

            Console.WriteLine("\n");

            if (args.Length == 2) //pass path and solution name via command args
            {
                solutionPath = args[0];
                solutionName = args[1];
            }
            else
            {
                Console.Write("\nEnter path to solution: ");
                solutionPath = Console.ReadLine();
                Console.Write("\nEnter solution name (.sln): ");
                solutionName = Console.ReadLine();
            }


            ReTarget reTarget = new ReTarget(solutionPath, solutionName, logger);

            reTarget.ConvertSolution();

            logger.Log("Next steps:");
            logger.Log("\tOpen Solution");
            logger.Log("\tBuild Solution (NuGet packages will get restored)");
            logger.Log("\tClose Solution");
            logger.Log("\tOpen Solution");
            logger.Log("\tManually add missing NuGet packages (see list, if any)");

            Console.ReadKey();
        }

        private static bool PrintDisclaimer()
        {
            Console.WriteLine("====DISCLAIMER====");
            Console.WriteLine("Please note:");
            Console.WriteLine("* This tool makes changes to the Projects in your Solution. Although the sofware is tested, it might potentially break your Project/Solution (although this is NOT the intent).");
            Console.WriteLine("* Under no circumstances, the author of this tool can be held responsible for any side-effects this tool might cause.");
            Console.WriteLine("* It is your own choise to use this tool!");
            Console.WriteLine("* We strongly recommand to first test this tool on a copy of your solution. Or you have at least a copy of your Solution you can get back to, in case anything might go wrong.");

            Console.Write("\nContinue? (y/n) ");
            return Console.ReadKey().KeyChar == 'y';  
        }

        public class ConsoleLogger : ILogger
        {
            public void Log(string message)
            {
                Console.WriteLine(message);
            }
        }
    }
}
