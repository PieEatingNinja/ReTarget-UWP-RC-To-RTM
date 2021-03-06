﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ReTarget
{
    //REF: https://msdn.microsoft.com/library/mt148501(v=vs.140).aspx

    public class ReTarget
    {
        readonly string SolutionPath, SolutionName;
        readonly ILogger Logger;
        const string SDKVersion = "10.0.10240.0";

        readonly XNamespace msbuildNamespace;

        public Dictionary<string, List<string>> ManuallyToRestoreNuGetPackages =
            new Dictionary<string, List<string>>();

        public ReTarget(string solutionPath, string solutionName) :
            this(solutionPath, solutionName, null)
        { }

        public ReTarget(string solutionPath, string solutionName, ILogger logger)
        {
            SolutionPath = solutionPath;
            SolutionName = CheckSolutionName(solutionName);
            Logger = logger;

            msbuildNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";
        }

        public void ConvertSolution()
        {
            IEnumerable<string> solutionContentLines = null;

            string fullSolutionPath = SolutionPath.Trim('/') + "/" + SolutionName;

            try
            {
                solutionContentLines = File.ReadLines(fullSolutionPath);
            }
            catch (FileNotFoundException)
            {
                Log($"Unable to open Solution {fullSolutionPath}. Check your path and solution name.");
                return;
            }

            foreach (var projectLine in solutionContentLines.Where(a => a.ToLower().StartsWith("project")))
            {
                HandleProjectLine(projectLine, SolutionPath, SDKVersion);
            }

            Log("Done!\n");

            LogMissingNuGetPackages();
        }

        private void LogMissingNuGetPackages()
        {
            if (ManuallyToRestoreNuGetPackages.Any())
            {
                Log("The following NuGet packages need to be manually restored:");
                foreach (var project in ManuallyToRestoreNuGetPackages)
                {
                    Log($"\tIn project {project.Key};");
                    foreach (var package in project.Value)
                    {
                        Log($"\t\t {package};");
                    }
                }
            }
        }

        void HandleProjectLine(string line, string solutionPath, string sdkversion)
        {
            try
            {
                string projectPath, fullProjectpath;
                string data = line.Substring(line.LastIndexOf("=") + 2);
                string[] dataArray = data.Split(',');
                projectPath = dataArray[0];
                fullProjectpath = System.IO.Path.Combine(solutionPath, dataArray[1].Trim().Trim('"'));

                Log($"Converting Project {projectPath}");

                if (!File.Exists(fullProjectpath))
                {
                    Log($"\tCould not find project {fullProjectpath}.");
                    return;
                }

                string projectDataStr = System.IO.File.ReadAllText(fullProjectpath);
                XDocument projectData = XDocument.Parse(projectDataStr);

                ChangePlatformVersion(projectData);

                ChangeSDKReference(projectData);

                DeleteEnsureNuGetPackageBuildImports(projectData);

                DeleteImportsElement(projectData, @"..\packages\Microsoft.Diagnostics.Tracing.EventSource.Redist.");
                DeleteImportsElement(projectData, @"..\packages\Microsoft.ApplicationInsights");

                bool restoreApplicationInsightsPck = DeleteLegacyNuGetIncludes(projectData, projectPath);

                EditOrAddProjectJsonInclude(projectData);

                AddProjectJsonFile(fullProjectpath.Substring(0, fullProjectpath.LastIndexOf("\\")), restoreApplicationInsightsPck);

                projectData.Save(fullProjectpath);
            }
            catch (Exception)
            {
                Log("\tConversion failed!");

            }
            finally
            {
                Log("--------------------");
            }
        }

        //Delete the file packages.config, and create a new .json file in this folder.Name the file: 
        //project.json
        private void AddProjectJsonFile(string projectPath, bool restoreInsightsPackage)
        {
            File.Delete(projectPath + "\\packages.config");
            using (var stream = File.CreateText(projectPath + "\\project.json"))
            {
                stream.WriteLine("{");
                stream.WriteLine("  \"dependencies\": {");

                if (restoreInsightsPackage)
                {
                    stream.WriteLine("    \"Microsoft.ApplicationInsights\": \"1.0.0\",");
                    stream.WriteLine("    \"Microsoft.ApplicationInsights.PersistenceChannel\": \"1.0.0\",");
                    stream.WriteLine("    \"Microsoft.ApplicationInsights.WindowsApps\": \"1.0.0\",");
                }

                stream.WriteLine("    \"Microsoft.NETCore.UniversalWindowsPlatform\": \"5.0.0\"");
                stream.WriteLine("  },");
                stream.WriteLine("  \"frameworks\": {");
                stream.WriteLine("    \"uap10.0\": { }");
                stream.WriteLine("  },");
                stream.WriteLine("  \"runtimes\": {");
                stream.WriteLine("    \"win10-arm\": {},");
                stream.WriteLine("    \"win10-arm-aot\": {},");
                stream.WriteLine("    \"win10-x86\": {},");
                stream.WriteLine("    \"win10-x86-aot\": {},");
                stream.WriteLine("    \"win10-x64\": {},");
                stream.WriteLine("    \"win10-x64-aot\": { }");
                stream.WriteLine("  }");
                stream.WriteLine("}");
            }
            Log("\tCreated project.json file");
        }

        //Find the<ItemGroup> element that contains an <AppxManifest> element.
        //If there is a <None> element with an Include attribute set to: packages.config, delete it. 
        //Also, add a <None> element with an Include attribute and set its value to: project.json.
        private void EditOrAddProjectJsonInclude(XDocument projectData)
        {
            var packagesConfig = projectData.Descendants(msbuildNamespace + "None").Where(a => (a.Attribute("Include")?.Value ?? "") == "packages.config").FirstOrDefault();
            if (packagesConfig != null)
            {
                packagesConfig.Attribute("Include").Value = "project.json";
                Log("\tUpdated Include from Packages.config to project.json");
            }
            else
            {
                //add project.json
                var itemgroup = new XElement(msbuildNamespace + "ItemGroup");
                var noneElement = new XElement(msbuildNamespace + "None");
                noneElement.Add(new XAttribute("Include", "project.json"));
                itemgroup.Add(noneElement);
                projectData.Descendants(msbuildNamespace + "PropertyGroup").Last().AddAfterSelf(itemgroup);
                Log("\tAdded Include for project.json");
            }
        }

        //Find the<ItemGroup> that has <Reference> children elements to NuGet packages.
        //Take note of the NuGet packages that are referenced, because you will need this 
        //information for a future step.One significant difference between the Windows 10 
        //project format between Visual Studio 2015 RC and Visual Studio 2015 RTM is that the 
        //RTM format uses NuGet version 3. 
        //Remove the <ItemGroup> and all its children.
        private bool DeleteLegacyNuGetIncludes(XDocument projectData, string projectPath)
        {
            bool shouldTryRestoreInsightsPckg = false;
            var nugetItemGroup = projectData.Descendants(msbuildNamespace + "ItemGroup").Where(a => a.Descendants(msbuildNamespace + "HintPath").Any()).FirstOrDefault();
            if (nugetItemGroup != null)
            {
                List<string> packages = new List<string>();
                Log($"\tNuGet references will be removed. After upgrading, add them manually.");
                foreach (var reference in nugetItemGroup.Elements(msbuildNamespace + "Reference"))
                {
                    var name = reference.Attribute("Include").Value.Substring(0, reference.Attribute("Include").Value.IndexOf(","));
                    Log($"\t\tNuGet reference {name}");
                    if (name.StartsWith("Microsoft.ApplicationInsights"))
                    {
                        Log($"\t\t{name} will be restored automatically...");
                        shouldTryRestoreInsightsPckg = true;
                    }
                    else
                    {
                        packages.Add(name);
                    }
                }
                ManuallyToRestoreNuGetPackages.Add(projectPath, packages);
                nugetItemGroup.Remove();
            }
            return shouldTryRestoreInsightsPckg;
        }

        //Find and delete the<Import> elements with Project and Condition attributes that 
        //reference Microsoft.Diagnostics.Tracing.EventSource and Microsoft.ApplicationInsights
        private void DeleteImportsElement(XDocument projectData, string projectStartHint)
        {
            var importElement = projectData.Descendants(msbuildNamespace + "Import").Where(a => (a.Attribute("Project")?.Value ?? "").StartsWith(projectStartHint)).FirstOrDefault();
            if (importElement != null)
            {
                importElement.Remove();
                Log($"\tDeleted Import element for {projectStartHint}");
            }
        }

        //Find the <Target> element with a name attribute that has the value: 
        //EnsureNuGetPackageBuildImports. Delete this element and all its children. 
        private void DeleteEnsureNuGetPackageBuildImports(XDocument projectData)
        {
            var targetEnsureNuGetPackageBuildImports = projectData.Descendants(msbuildNamespace + "Target").Where(a => (a.Attribute("Name")?.Value ?? "") == "EnsureNuGetPackageBuildImports");
            if (targetEnsureNuGetPackageBuildImports != null)
            {
                targetEnsureNuGetPackageBuildImports.Remove();
                Log($"\tRemoved EnsureNuGetPackageBuildImports element");
            }
        }

        //If you added any references to UWP Extension SDKs(for example: the Windows Mobile SDK), 
        //you will need to update the SDK version.For example this <SDKReference> element
        private void ChangeSDKReference(XDocument projectData)
        {
            var WindowsMobileRef = projectData.Descendants(msbuildNamespace + "SDKReference").Where(a => a.Attribute("Include").Value.StartsWith("WindowsMobile")).FirstOrDefault();
            var WindowsDesktopRef = projectData.Descendants(msbuildNamespace + "SDKReference").Where(a => a.Attribute("Include").Value.StartsWith("WindowsDesktop")).FirstOrDefault();

            if (WindowsMobileRef != null)
            {
                WindowsMobileRef.Attribute("Include").Value = $"WindowsMobile, Version={SDKVersion}";
                Log("\tUpdated WindowsMobile Reference to SDK Version {0}", SDKVersion);
            }

            if (WindowsDesktopRef != null)
            {
                WindowsDesktopRef.Attribute("Include").Value = $"WindowsDesktop, Version={SDKVersion}";
                Log("\tUpdated WindowsDesktop Reference to SDK Version {0}", SDKVersion);
            }
        }

        //Find the<PropertyGroup> element that contains the <TargetPlatformVersion> 
        //and<TargetPlatformMinVersion> elements. Change the existing value of the 
        //<TargetPlatformVersion> and<TargetPlatformMinVersion> elements to be the same 
        //version of the Universal Windows Platform that you have installed. 
        private void ChangePlatformVersion(XDocument projectData)
        {
            var targetPlatformElement = projectData.Element(msbuildNamespace + "Project").Element(msbuildNamespace + "PropertyGroup").Element(msbuildNamespace + "TargetPlatformVersion");
            var targetPlatformMinElement = projectData.Element(msbuildNamespace + "Project").Element(msbuildNamespace + "PropertyGroup").Element(msbuildNamespace + "TargetPlatformMinVersion");

            ChangeVersion(targetPlatformElement, "TargetPlatformVersion", SDKVersion);
            ChangeVersion(targetPlatformMinElement, "TargetPlatformMinVersion", SDKVersion);
        }

        private bool ChangeVersion(XElement element, string name, string newVersion)
        {
            if (element != null)
            {
                string currentValue = element.Value;
                element.Value = newVersion;
                Log($"\tChanged version {name} from '{currentValue}' to '{newVersion}'");
                return true;
            }
            else
            {
                Log($"\t{name} not found!");
                return false;
            }
        }

        private string CheckSolutionName(string solutionName)
        {
            if (!solutionName.EndsWith(".sln"))
            {
                return string.Format("{0}.sln", solutionName);
            }
            return solutionName;
        }

        private void Log(string message, params object[] args)
        {
            if (Logger != null)
                Logger.Log(string.Format(message, args));
        }
    }
}
