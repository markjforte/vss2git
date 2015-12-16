using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Hpdi.VssLogicalLib;
using Hpdi.VssPhysicalLib;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Enumeates files in a VSS database and builds a sorted dictionary that contains each each file's active project
    /// </summary>
    /// <author>Mark Forte</author>
    class FileAnalyzer : Worker
    {
        private readonly ProjectAnalyzer projectAnalyzer;
        public string vssRootProjectPath
        {
            get { return projectAnalyzer.vssRootProjectPath; } 
        }

        private string excludeFiles;
        public string ExcludeFiles
        {
            get { return excludeFiles; }
            set { excludeFiles = value; }
        }

        private readonly SortedDictionary<String, FileLocation> sortedFileLocations =
            new SortedDictionary<String, FileLocation>();
        public SortedDictionary<String, FileLocation> SortedFileLocations
        {
            get { return sortedFileLocations; }
        }
        
        private readonly HashSet<string> processedFiles = new HashSet<string>();
        public HashSet<string> ProcessedFiles
        {
            get { return processedFiles; }
        }

        private int fileCount;
        public int FileCount
        {
            get { return Thread.VolatileRead(ref fileCount); }
        }

        private int sharedFileCount;
        public int SharedFileCount
        {
            get { return Thread.VolatileRead(ref sharedFileCount); }
        }

        public FileAnalyzer(WorkQueue workQueue, Logger logger, ProjectAnalyzer projectAnalyzer)
            : base(workQueue, logger)
        {
            this.projectAnalyzer = projectAnalyzer;
        }

        public void AddItem(VssProject project)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }

            PathMatcher exclusionMatcher = null;
            if (!string.IsNullOrEmpty(excludeFiles))
            {
                var excludeFileArray = excludeFiles.Split(
                    new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                exclusionMatcher = new PathMatcher(excludeFileArray);
            }

            workQueue.AddLast(delegate(object work)
            {
                logger.WriteSectionSeparator();
                LogStatus(work, "Building file location dictionary");

                logger.WriteLine("Root project: {0}", project.Path);
                logger.WriteLine("Excluded files: {0}", excludeFiles);

                int excludedFiles = 0;
                var stopwatch = Stopwatch.StartNew();
                VssUtil.RecurseItems(project,
                    delegate(VssProject subproject)
                    {
                        if (workQueue.IsAborting)
                        {
                            return RecursionStatus.Abort;
                        }
                        return RecursionStatus.Continue;
                    },
                    delegate(VssProject subproject, VssFile file)
                    {
                        if (workQueue.IsAborting)
                        {
                            return RecursionStatus.Abort;
                        }

                        var path = file.GetPath(subproject);
                        if (exclusionMatcher != null && exclusionMatcher.Matches(path))
                        {
                            logger.WriteLine("Excluding file {0}", path);
                            ++excludedFiles;
                            return RecursionStatus.Skip;
                        }

                        // Don't process files in deleted projects
                        if (!projectAnalyzer.DeletedProjects.Contains(subproject.PhysicalName))
                        {
                            ProjectLocation projectLocation;
                            if (!projectAnalyzer.SortedProjectLocations.TryGetValue(subproject.PhysicalName, out projectLocation))
                            {
                                // If the project is not found it is (i.e. should) be due to exclusionMatcher
                                //logger.WriteLine("Unexpected: FileAnalyzer: SortedProjectLocations does not contain project: {0}", subproject.PhysicalName);
                            }
                            else if (!projectLocation.DeletedFiles.Contains(file.PhysicalName))
                            {
                                // If the file is shared it might be in more than one project... 
                                // But this should not happen using this alternate import method!
                                if (processedFiles.Contains(file.PhysicalName))
                                {
                                    ++sharedFileCount;
                                    logger.WriteLine("Unexpected: FileAnalyzer: File shared in more tha one project: {0}: {1}", file.PhysicalName, subproject.Path);
                                }
                                else
                                {
                                    processedFiles.Add(file.PhysicalName);
                                    FileLocation fileLocation;
                                    if (sortedFileLocations.TryGetValue(file.PhysicalName, out fileLocation))
                                        logger.WriteLine("Unexpected: FileAnalyzer: sortedFileLocations already contains file: {0}", file.PhysicalName);
                                    else
                                    {
                                        fileLocation = new FileLocation(file.PhysicalName, file.GetPath(subproject));
                                        sortedFileLocations[file.PhysicalName] = fileLocation;
                                    }
                                    ++fileCount;
                                }
                            }
                        }

                        return RecursionStatus.Continue;
                    });
                stopwatch.Stop();

                logger.WriteSectionSeparator();
                logger.WriteLine("Analysis complete in {0:HH:mm:ss}", new DateTime(stopwatch.ElapsedTicks));
                logger.WriteLine("Files: {0} ({1} excluded)", fileCount, excludedFiles);
                logger.WriteLine("Shared Files: {0}", sharedFileCount);

                if (sharedFileCount > 0)
                {
                    workQueue.Abort();
                    MessageBox.Show("Shared files exist!  " +
                        "This alternate logic depends on files existing in only one location.  " +
                        "Please resolve before reattempting conversion.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }
    }
}
