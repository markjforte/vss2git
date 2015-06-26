using System;
using System.Collections.Generic;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Represents the location of a project
    /// </summary>
    /// <author>Mark Forte</author>
    class ProjectLocation
    {
        private readonly string physicalName;
        public string PhysicalName
        {
            get { return physicalName; }
        }

        private string path;
        public string Path        
        {
            get { return path; }
        }

        private readonly HashSet<string> deletedFiles = new HashSet<string>();
        public HashSet<string> DeletedFiles
        {
            get { return deletedFiles; }
        }

        public ProjectLocation(string physicalName, string path)
        {
            this.physicalName = physicalName;
            this.path = path;
        }
    }
}