﻿// <copyright file="ParsedProject.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection.Metadata;
    using System.Reflection.PortableExecutable;
    using Microsoft.Build.Evaluation;
    using Microsoft.Build.Execution;
    using Microsoft.Extensions.Logging;
    using NuGet.Common;
    using NuGet.Frameworks;
    using NuGet.ProjectModel;
    using ILogger = Microsoft.Extensions.Logging.ILogger;

    internal sealed class ParsedProject
    {
        private static readonly Dictionary<string, ParsedProject> Projects = new Dictionary<string, ParsedProject>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] RestoreTargets = { "Restore" };
        private static readonly string[] CompileTargets = { "Compile" };

        private ParsedProject()
        {
        }

        public string Name { get; private set; }

        public string AssemblyName { get; private set; }

        public HashSet<string> AssemblyReferences { get; private set; }

        public List<string> References { get; private set; }

        public List<ProjectReference> ProjectReferences { get; private set; }

        public List<string> PackageReferences { get; private set; }

        public Dictionary<string, List<string>> PackageAssemblies { get; private set; }

        public static ParsedProject Create(
            string projectFile,
            Arguments arguments,
            BuildManager buildManager,
            ILogger logger)
        {
            if (!Projects.TryGetValue(projectFile, out var project))
            {
                project = CreateInternal(projectFile, arguments, buildManager, logger);
                Projects.Add(projectFile, project);
            }

            return project;
        }

        private static ParsedProject CreateInternal(
            string projectFile,
            Arguments arguments,
            BuildManager buildManager,
            ILogger logger)
        {
            var relativeProjectFile = projectFile.Substring(arguments.Path.Length + 1);
            try
            {
                var project = new Project(projectFile);

                var assemblyFile = project.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude;
                if (string.IsNullOrEmpty(assemblyFile))
                {
                    // Not all projects may produce an assembly. Just avoid these sorts of projects.
                    return null;
                }

                var projectDirectory = Path.GetDirectoryName(projectFile);
                var assemblyFileFullPath = Path.GetFullPath(Path.Combine(projectDirectory, assemblyFile));
                var assemblyFileRelativePath = TryMakeRelative(arguments.Path, assemblyFileFullPath);

                // Compile the assembly if needed
                if (!File.Exists(assemblyFileFullPath))
                {
                    if (arguments.CompileIfNeeded)
                    {
                        logger.LogDebug($"Assembly {assemblyFileRelativePath} does not exist. Compiling {relativeProjectFile}...");
                        var projectInstance = project.CreateProjectInstance();

                        // Compile usually requires a restore as well
                        if (arguments.RestoreIfNeeded)
                        {
                            var restoreResult = ExecuteRestore(projectInstance, buildManager);
                            if (restoreResult.OverallResult != BuildResultCode.Success)
                            {
                                logger.LogError($"Project failed to restore: {relativeProjectFile}");
                                return null;
                            }
                        }

                        var compileResult = ExecuteCompile(projectInstance, buildManager);
                        if (compileResult.OverallResult != BuildResultCode.Success)
                        {
                            logger.LogError($"Project failed to compile: {relativeProjectFile}");
                            return null;
                        }
                    }
                    else
                    {
                        // Can't analyze this project since it hasn't been built
                        logger.LogError($"Assembly {assemblyFileRelativePath} did not exist. Ensure you've previously built it, or set the --CompileIfNeeded flag. Project: {relativeProjectFile}");
                        return null;
                    }
                }

                // Read metadata from the assembly, such as the assembly name and its references
                string assemblyName;
                var assemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var stream = File.OpenRead(assemblyFileFullPath))
                using (var peReader = new PEReader(stream))
                {
                    var metadata = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
                    if (!metadata.IsAssembly)
                    {
                        logger.LogError($"{assemblyFileRelativePath} is not an assembly");
                        return null;
                    }

                    assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);

                    foreach (var assemblyReferenceHandle in metadata.AssemblyReferences)
                    {
                        var reference = metadata.GetAssemblyReference(assemblyReferenceHandle);
                        var name = metadata.GetString(reference.Name);
                        if (!string.IsNullOrEmpty(name))
                        {
                            assemblyReferences.Add(name);
                        }
                    }
                }

                var references = project
                    .GetItems("Reference")
                    .Where(reference => !reference.UnevaluatedInclude.Equals("@(_SDKImplicitReference)", StringComparison.OrdinalIgnoreCase))
                    .Select(reference => reference.EvaluatedInclude)
                    .ToList();

                var projectReferences = project
                    .GetItems("ProjectReference")
                    .Select(reference => new ProjectReference(Create(Path.GetFullPath(Path.Combine(projectDirectory, reference.EvaluatedInclude)), arguments, buildManager, logger), reference.UnevaluatedInclude))
                    .Where(projectReference => projectReference.Project != null)
                    .ToList();

                var packageReferences = project
                    .GetItems("PackageReference")
                    .Select(reference => reference.EvaluatedInclude)
                    .ToList();

                // Certain project types may require references simply to copy them to the output folder to satisfy transitive dependencies.
                if (NeedsTransitiveAssemblyReferences(project))
                {
                    projectReferences.ForEach(projectReference => assemblyReferences.UnionWith(projectReference.Project.AssemblyReferences));
                }

                // Collect package assemblies
                var packageAssemblies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (packageReferences.Count > 0)
                {
                    var projectAssetsFile = project.GetPropertyValue("ProjectAssetsFile");
                    if (string.IsNullOrEmpty(projectAssetsFile))
                    {
                        logger.LogError($"Project with PackageReferences missing ProjectAssetsFile property: {relativeProjectFile}");
                        return null;
                    }

                    // TODO: Combine with the restore above.
                    var projectAssetsFileFullPath = Path.GetFullPath(Path.Combine(projectDirectory, projectAssetsFile));
                    var projectAssetsFileRelativePath = TryMakeRelative(arguments.Path, projectAssetsFileFullPath);
                    if (!File.Exists(projectAssetsFileFullPath))
                    {
                        if (arguments.RestoreIfNeeded)
                        {
                            logger.LogDebug($"ProjectAssetsFile {projectAssetsFileRelativePath} did not exist. Restoring {relativeProjectFile}...");
                            var projectInstance = project.CreateProjectInstance();

                            var restoreResult = ExecuteRestore(projectInstance, buildManager);
                            if (restoreResult.OverallResult != BuildResultCode.Success)
                            {
                                logger.LogError($"Project failed to restore: {relativeProjectFile}");
                                return null;
                            }
                        }
                        else
                        {
                            // Can't analyze this project since it hasn't been restored
                            logger.LogError($"ProjectAssetsFile {projectAssetsFileRelativePath} did not exist. Ensure you've previously built it, or set the --RestoreIfNeeded flag. Project: {relativeProjectFile}");
                            return null;
                        }
                    }

                    var lockFile = LockFileUtilities.GetLockFile(projectAssetsFileFullPath, NullLogger.Instance);
                    if (lockFile == null)
                    {
                        logger.LogError($"{projectAssetsFileRelativePath} is not a valid assets file");
                        return null;
                    }

                    var packageFolders = lockFile.PackageFolders.Select(item => item.Path).ToList();

                    var nuGetTargetMoniker = project.GetPropertyValue("NuGetTargetMoniker");
                    var runtimeIdentifier = project.GetPropertyValue("RuntimeIdentifier");

                    var nugetTarget = lockFile.GetTarget(NuGetFramework.Parse(nuGetTargetMoniker), runtimeIdentifier);
                    var nugetLibraries = nugetTarget.Libraries
                        .Where(nugetLibrary => nugetLibrary.Type.Equals("Package", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Compute the hierarchy of packages.
                    // Keys are packages and values are packages which depend on that package.
                    var nugetDependants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var nugetLibrary in nugetLibraries)
                    {
                        var packageId = nugetLibrary.Name;
                        foreach (var dependency in nugetLibrary.Dependencies)
                        {
                            if (!nugetDependants.TryGetValue(dependency.Id, out var parents))
                            {
                                parents = new List<string>();
                                nugetDependants.Add(dependency.Id, parents);
                            }

                            parents.Add(packageId);
                        }
                    }

                    // Get the transitive closure of assemblies included by each package
                    foreach (var nugetLibrary in nugetLibraries)
                    {
                        var nugetLibraryAssemblies = nugetLibrary.CompileTimeAssemblies
                            .Select(item => item.Path)
                            .Where(path => !path.EndsWith("_._", StringComparison.Ordinal)) // Ignore special packages
                            .Select(path =>
                            {
                                var packageFolderRelativePath = Path.Combine(nugetLibrary.Name, nugetLibrary.Version.ToNormalizedString(), path);
                                var fullPath = packageFolders
                                    .Select(packageFolder => Path.Combine(packageFolder, packageFolderRelativePath))
                                    .First(File.Exists);
                                return System.Reflection.AssemblyName.GetAssemblyName(fullPath).Name;
                            })
                            .ToList();

                        // Walk up to add assemblies to all packages which directly or indirectly depend on this one.
                        var seenDependants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var queue = new Queue<string>();
                        queue.Enqueue(nugetLibrary.Name);
                        while (queue.Count > 0)
                        {
                            var packageId = queue.Dequeue();

                            // Add this package's assemblies, if there are any
                            if (nugetLibraryAssemblies.Count > 0)
                            {
                                if (!packageAssemblies.TryGetValue(packageId, out var assemblies))
                                {
                                    assemblies = new List<string>();
                                    packageAssemblies.Add(packageId, assemblies);
                                }

                                assemblies.AddRange(nugetLibraryAssemblies);
                            }

                            // Recurse though dependants
                            if (nugetDependants.TryGetValue(packageId, out var dependants))
                            {
                                foreach (var dependant in dependants)
                                {
                                    if (seenDependants.Add(dependant))
                                    {
                                        queue.Enqueue(dependant);
                                    }
                                }
                            }
                        }
                    }
                }

                return new ParsedProject
                {
                    Name = projectFile,
                    AssemblyName = assemblyName,
                    AssemblyReferences = assemblyReferences,
                    References = references,
                    ProjectReferences = projectReferences,
                    PackageReferences = packageReferences,
                    PackageAssemblies = packageAssemblies,
                };
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception e)
            {
                logger.LogError($"Exception while trying to load: {relativeProjectFile}. Exception: {e}");
                return null;
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        private static string TryMakeRelative(string baseDirectory, string maybeFullPath)
        {
            if (baseDirectory[baseDirectory.Length - 1] != Path.DirectorySeparatorChar)
            {
                baseDirectory += Path.DirectorySeparatorChar;
            }

            return maybeFullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase)
                ? maybeFullPath.Substring(baseDirectory.Length)
                : maybeFullPath;
        }

        private static bool NeedsTransitiveAssemblyReferences(Project projectInstance)
        {
            var outputType = projectInstance.GetPropertyValue("OutputType");
            if (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        // Based on MSBuild.exe's restore logic when using /restore. https://github.com/Microsoft/msbuild/blob/master/src/MSBuild/XMake.cs#L1242
        private static BuildResult ExecuteRestore(ProjectInstance projectInstance, BuildManager buildManager)
        {
            const string UniqueProperty = "MSBuildRestoreSessionId";

            // Set a property with a random value to ensure that restore happens under a different evaluation context
            // If the evaluation context is not different, then projects won't be re-evaluated after restore
            projectInstance.SetProperty(UniqueProperty, Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));

            // Create a new request with a Restore target only and specify:
            //  - BuildRequestDataFlags.ClearCachesAfterBuild to ensure the projects will be reloaded from disk for subsequent builds
            //  - BuildRequestDataFlags.SkipNonexistentTargets to ignore missing targets since Restore does not require that all targets exist
            //  - BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports to ignore imports that don't exist, are empty, or are invalid because restore might
            //     make available an import that doesn't exist yet and the <Import /> might be missing a condition.
            var request = new BuildRequestData(
                projectInstance,
                targetsToBuild: RestoreTargets,
                hostServices: null,
                flags: BuildRequestDataFlags.ClearCachesAfterBuild | BuildRequestDataFlags.SkipNonexistentTargets | BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports);

            var result = ExecuteBuild(buildManager, request);

            // Revert the property
            projectInstance.RemoveProperty(UniqueProperty);

            return result;
        }

        private static BuildResult ExecuteCompile(ProjectInstance projectInstance, BuildManager buildManager) => ExecuteBuild(buildManager, new BuildRequestData(projectInstance, CompileTargets));

        private static BuildResult ExecuteBuild(BuildManager buildManager, BuildRequestData request) => buildManager.PendBuildRequest(request).Execute();
    }
}
