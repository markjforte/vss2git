using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Hpdi.VssLogicalLib;
using Hpdi.VssPhysicalLib;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Enumeates projects in a VSS database and builds a sorted dictionary of the active projects and their locations
    /// </summary>
    /// <author>Mark Forte</author>
    class ProjectAnalyzer : Worker
    {
        private string excludeFiles;
        public string ExcludeFiles
        {
            get { return excludeFiles; }
            set { excludeFiles = value; }
        }

        private readonly VssDatabase database;
        public VssDatabase Database
        {
            get { return database; }
        }

        private readonly LinkedList<VssProject> rootProjects = new LinkedList<VssProject>();
        public IEnumerable<VssProject> RootProjects
        {
            get { return rootProjects; }
        }

        private readonly SortedDictionary<String, ProjectLocation> sortedProjectLocations =
            new SortedDictionary<String, ProjectLocation>();
        public SortedDictionary<String, ProjectLocation> SortedProjectLocations
        {
            get { return sortedProjectLocations; }
        }

        private readonly HashSet<string> deletedProjects = new HashSet<string>();
        public HashSet<string> DeletedProjects
        {
            get { return deletedProjects; }
        }

        private int projectCount;
        public int ProjectCount
        {
            get { return Thread.VolatileRead(ref projectCount); }
        }

        public ProjectAnalyzer(WorkQueue workQueue, Logger logger, VssDatabase database)
            : base(workQueue, logger)
        {
            this.database = database;
        }

        public void AddItem(VssProject project)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }
            else if (project.Database != database)
            {
                throw new ArgumentException("Project database mismatch", "project");
            }

            rootProjects.AddLast(project);

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
                LogStatus(work, "Building active project list");

                logger.WriteLine("Root project: {0}", project.Path);
                logger.WriteLine("Excluded files: {0}", excludeFiles);

                int excludedProjects = 0;
                var stopwatch = Stopwatch.StartNew();
                VssUtil.RecurseItems(project,
                    delegate(VssProject subproject)
                    {
                        if (workQueue.IsAborting)
                        {
                            return RecursionStatus.Abort;
                        }

                        var path = subproject.Path;
                        if (exclusionMatcher != null && exclusionMatcher.Matches(path))
                        {
                            logger.WriteLine("Excluding project {0}", path);
                            ++excludedProjects;
                            return RecursionStatus.Skip;
                        }

                        if (!deletedProjects.Contains(subproject.PhysicalName))
                        {
                            ProcessProject(subproject, path, exclusionMatcher);
                            ++projectCount;
                        }

                        return RecursionStatus.Continue;
                    },
                    delegate(VssProject subproject, VssFile file)
                    {
                        if (workQueue.IsAborting)
                        {
                            return RecursionStatus.Abort;
                        }
                        return RecursionStatus.Continue;
                    });
                stopwatch.Stop();

                logger.WriteSectionSeparator();
                logger.WriteLine("Analysis complete in {0:HH:mm:ss}", new DateTime(stopwatch.ElapsedTicks));
                logger.WriteLine("Projects: {0} ({1} excluded)", projectCount, excludedProjects);
            });
        }

        private void ProcessProject(VssProject project, string path, PathMatcher exclusionMatcher)
        {
            try
            {
                ProjectLocation projectLocation;
                if (sortedProjectLocations.TryGetValue(project.PhysicalName, out projectLocation))
                    logger.WriteLine("Unexpected: ProjectAnalyzer.ProcessProject: sortedProjectLocations already contains project: {0}", project.PhysicalName);
                else
                {
                    projectLocation = new ProjectLocation(project.PhysicalName, path);
                    sortedProjectLocations[project.PhysicalName] = projectLocation;
                };

                foreach (VssRevision vssRevision in project.Revisions)
                {
                    var actionType = vssRevision.Action.Type;
                    var namedAction = vssRevision.Action as VssNamedAction;
                    if (namedAction != null)
                    {
                        var targetPath = path + VssDatabase.ProjectSeparator + namedAction.Name.LogicalName;
                        if (exclusionMatcher != null && exclusionMatcher.Matches(targetPath))
                        {
                            // project action targets an excluded file
                            continue;
                        }

                        if (namedAction.Name.IsProject)
                        {
                            if (
                                (actionType == VssActionType.Delete) ||
                                (actionType == VssActionType.Destroy)
                            )
                                deletedProjects.Add(namedAction.Name.PhysicalName);
                            else if (
                                (actionType == VssActionType.Recover) ||
                                (actionType == VssActionType.Share)
                            )
                                deletedProjects.Remove(namedAction.Name.PhysicalName);
                        }
                        else
                        {
                            if (
                                (actionType == VssActionType.Delete) ||
                                (actionType == VssActionType.Destroy)
                            )
                                projectLocation.DeletedFiles.Add(namedAction.Name.PhysicalName);
                            else if (
                                (actionType == VssActionType.Recover) ||
                                (actionType == VssActionType.Share)
                            )
                                projectLocation.DeletedFiles.Remove(namedAction.Name.PhysicalName);
                        }

                    }
                }
            }
            catch (RecordException e)
            {
                var message = string.Format("ProjectAnalyzer.ProcessProject: Failed to process project for {0} ({1}): {2}",
                    path, project.PhysicalName, ExceptionFormatter.Format(e));
                LogException(e, message);
                ReportError(message);
            }
        }
    }
}
