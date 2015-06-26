/* Copyright 2009 HPDI, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Hpdi.VssLogicalLib;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Replays and commits changesets into a new Git repository.
    /// </summary>
    /// <author>Trevor Robinson</author>
    class GitExporterAlt : Worker
    {
        private const string DefaultComment = "Vss2Git";

        private readonly VssDatabase database;
        private readonly RevisionAnalyzer revisionAnalyzer;
        private readonly ChangesetBuilder changesetBuilder;
        private readonly StreamCopier streamCopier = new StreamCopier();
        private readonly HashSet<string> tagsUsed = new HashSet<string>();
        private readonly string repoPath;
        private readonly FileAnalyzer fileAnalyzer;

        private string emailDomain = "localhost";
        public string EmailDomain
        {
            get { return emailDomain; }
            set { emailDomain = value; }
        }

        private Encoding commitEncoding = Encoding.UTF8;
        public Encoding CommitEncoding
        {
            get { return commitEncoding; }
            set { commitEncoding = value; }
        }

        private bool forceAnnotatedTags = true;
        public bool ForceAnnotatedTags
        {
            get { return forceAnnotatedTags; }
            set { forceAnnotatedTags = value; }
        }

        public GitExporterAlt(WorkQueue workQueue, Logger logger, 
            RevisionAnalyzer revisionAnalyzer, ChangesetBuilder changesetBuilder,
            string repoPath, FileAnalyzer fileAnalyzer)
            : base(workQueue, logger)
        {
            this.database = revisionAnalyzer.Database;
            this.revisionAnalyzer = revisionAnalyzer;
            this.changesetBuilder = changesetBuilder;
            this.repoPath = repoPath;
            this.fileAnalyzer = fileAnalyzer;
        }

        public void ExportToGit()
        {
            workQueue.AddLast(delegate(object work)
            {
                var stopwatch = Stopwatch.StartNew();

                logger.WriteSectionSeparator();
                LogStatus(work, "Initializing Git repository");

                // create repository directory if it does not exist
                if (!Directory.Exists(repoPath))
                {
                    Directory.CreateDirectory(repoPath);
                }

                var git = new GitWrapper(repoPath, logger);
                git.CommitEncoding = commitEncoding;

                while (!git.FindExecutable())
                {
                    var button = MessageBox.Show("Git not found in PATH. " +
                        "If you need to modify your PATH variable, please " +
                        "restart the program for the changes to take effect.",
                        "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                    if (button == DialogResult.Cancel)
                    {
                        workQueue.Abort();
                        return;
                    }
                }

                if (!RetryCancel(delegate { git.Init(); }))
                {
                    return;
                }

                if (commitEncoding.WebName != "utf-8")
                {
                    AbortRetryIgnore(delegate
                    {
                        git.SetConfig("i18n.commitencoding", commitEncoding.WebName);
                    });
                }

                // replay each changeset
                var changesetId = 1;
                var changesets = changesetBuilder.Changesets;
                var commitCount = 0;
                var tagCount = 0;
                var replayStopwatch = new Stopwatch();
                var labels = new LinkedList<Revision>();
                tagsUsed.Clear();
                foreach (var changeset in changesets)
                {
                    var changesetDesc = string.Format(CultureInfo.InvariantCulture,
                        "changeset {0} from {1}", changesetId, changeset.DateTime);

                    // replay each revision in changeset
                    LogStatus(work, "Replaying " + changesetDesc);
                    labels.Clear();
                    replayStopwatch.Start();
                    bool needCommit;
                    try
                    {
                        needCommit = ReplayChangeset(changeset, git, labels);
                    }
                    finally
                    {
                        replayStopwatch.Stop();
                    }

                    if (workQueue.IsAborting)
                    {
                        return;
                    }

                    // commit changes
                    if (needCommit)
                    {
                        LogStatus(work, "Committing " + changesetDesc);
                        if (CommitChangeset(git, changeset))
                        {
                            ++commitCount;
                        }
                    }

                    if (workQueue.IsAborting)
                    {
                        return;
                    }

                    // create tags for any labels in the changeset
                    if (labels.Count > 0)
                    {
                        foreach (Revision label in labels)
                        {
                            var labelName = ((VssLabelAction)label.Action).Label;
                            if (string.IsNullOrEmpty(labelName))
                            {
                                logger.WriteLine("NOTE: Ignoring empty label");
                            }
                            else if (commitCount == 0)
                            {
                                logger.WriteLine("NOTE: Ignoring label '{0}' before initial commit", labelName);
                            }
                            else
                            {
                                var tagName = GetTagFromLabel(labelName);

                                var tagMessage = "Creating tag " + tagName;
                                if (tagName != labelName)
                                {
                                    tagMessage += " for label '" + labelName + "'";
                                }
                                LogStatus(work, tagMessage);

                                // annotated tags require (and are implied by) a tag message;
                                // tools like Mercurial's git converter only import annotated tags
                                var tagComment = label.Comment;
                                if (string.IsNullOrEmpty(tagComment) && forceAnnotatedTags)
                                {
                                    // use the original VSS label as the tag message if none was provided
                                    tagComment = labelName;
                                }

                                if (AbortRetryIgnore(
                                    delegate
                                    {
                                        git.Tag(tagName, label.User, GetEmail(label.User),
                                            tagComment, label.DateTime);
                                    }))
                                {
                                    ++tagCount;
                                }
                            }
                        }
                    }

                    ++changesetId;
                }

                stopwatch.Stop();

                logger.WriteSectionSeparator();
                logger.WriteLine("Git export complete in {0:HH:mm:ss}", new DateTime(stopwatch.ElapsedTicks));
                logger.WriteLine("Replay time: {0:HH:mm:ss}", new DateTime(replayStopwatch.ElapsedTicks));
                logger.WriteLine("Git time: {0:HH:mm:ss}", new DateTime(git.ElapsedTime.Ticks));
                logger.WriteLine("Git commits: {0}", commitCount);
                logger.WriteLine("Git tags: {0}", tagCount);
            });
        }

        private bool ReplayChangeset(Changeset changeset,
            GitWrapper git, LinkedList<Revision> labels)
        {
            var needCommit = false;
            foreach (Revision revision in changeset.Revisions)
            {
                if (workQueue.IsAborting)
                {
                    break;
                }

                AbortRetryIgnore(delegate
                {
                    needCommit |= ReplayRevision(revision, git, labels);
                });
            }
            return needCommit;
        }

        private bool ReplayRevision(Revision revision,
            GitWrapper git, LinkedList<Revision> labels)
        {
            var needCommit = false;
            var actionType = revision.Action.Type;
            // We don't worry about projects! <smile>
            if (
                (!revision.Item.IsProject) 
                && 
                (actionType == VssActionType.Create) || (actionType == VssActionType.Edit)
            )
            {
                FileLocation fileLocation;
                if (fileAnalyzer.SortedFileLocations.TryGetValue(revision.Item.PhysicalName, out fileLocation))
                {
                    var path = VssPathMapper.GetWorkingPath(repoPath, fileLocation.Path);
                    logger.WriteLine("{0}: {1} revision {2}", fileLocation.Path, actionType, revision.Version);
                    if (WriteRevisionTo(revision.Item.PhysicalName, revision.Version, path))
                    {
                        // add file explicitly, so it is visible to subsequent git operations
                        git.Add(path);
                        needCommit = true;
                    }
                }
            }
            return needCommit;
        }

        private bool CommitChangeset(GitWrapper git, Changeset changeset)
        {
            var result = false;
            AbortRetryIgnore(delegate
            {
                result = git.AddAll() &&
                    git.Commit(changeset.User, GetEmail(changeset.User),
                    changeset.Comment ?? DefaultComment, changeset.DateTime);
            });
            return result;
        }

        private bool RetryCancel(ThreadStart work)
        {
            return AbortRetryIgnore(work, MessageBoxButtons.RetryCancel);
        }

        private bool AbortRetryIgnore(ThreadStart work)
        {
            return AbortRetryIgnore(work, MessageBoxButtons.AbortRetryIgnore);
        }

        private bool AbortRetryIgnore(ThreadStart work, MessageBoxButtons buttons)
        {
            bool retry;
            do
            {
                try
                {
                    work();
                    return true;
                }
                catch (Exception e)
                {
                    var message = LogException(e);

                    message += "\nSee log file for more information.";

                    var button = MessageBox.Show(message, "Error", buttons, MessageBoxIcon.Error);
                    switch (button)
                    {
                        case DialogResult.Retry:
                            retry = true;
                            break;
                        case DialogResult.Ignore:
                            retry = false;
                            break;
                        default:
                            retry = false;
                            workQueue.Abort();
                            break;
                    }
                }
            } while (retry);
            return false;
        }

        private string GetEmail(string user)
        {
            // TODO: user-defined mapping of user names to email addresses
            return user.ToLower().Replace(' ', '.') + "@" + emailDomain;
        }

        private string GetTagFromLabel(string label)
        {
            // git tag names must be valid filenames, so replace sequences of
            // invalid characters with an underscore
            var baseTag = Regex.Replace(label, "[^A-Za-z0-9_-]+", "_");

            // git tags are global, whereas VSS tags are local, so ensure
            // global uniqueness by appending a number; since the file system
            // may be case-insensitive, ignore case when hashing tags
            var tag = baseTag;
            for (int i = 2; !tagsUsed.Add(tag.ToUpperInvariant()); ++i)
            {
                tag = baseTag + "-" + i;
            }

            return tag;
        }

        private bool WriteRevisionTo(string physical, int version, string destPath)
        {
            VssFile item;
            VssFileRevision revision;
            Stream contents;
            try
            {
                item = (VssFile)database.GetItemPhysical(physical);
                revision = item.GetRevision(version);
                contents = revision.GetContents();
            }
            catch (Exception e)
            {
                // log an error for missing data files or versions, but keep processing
                var message = ExceptionFormatter.Format(e);
                logger.WriteLine("ERROR: {0}", message);
                logger.WriteLine(e);
                return false;
            }

            // propagate exceptions here (e.g. disk full) to abort/retry/ignore
            using (contents)
            {
                WriteStream(contents, destPath);
            }

            // try to use the first revision (for this branch) as the create time,
            // since the item creation time doesn't seem to be meaningful
            var createDateTime = item.Created;
            using (var revEnum = item.Revisions.GetEnumerator())
            {
                if (revEnum.MoveNext())
                {
                    createDateTime = revEnum.Current.DateTime;
                }
            }

            // set file creation and update timestamps
            File.SetCreationTimeUtc(destPath, TimeZoneInfo.ConvertTimeToUtc(createDateTime));
            File.SetLastWriteTimeUtc(destPath, TimeZoneInfo.ConvertTimeToUtc(revision.DateTime));

            return true;
        }

        private void WriteStream(Stream inputStream, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using (var outputStream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                streamCopier.Copy(inputStream, outputStream);
            }
        }
    }
}
