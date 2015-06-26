using System;
using System.Collections.Generic;
using Hpdi.VssLogicalLib;
using Hpdi.VssPhysicalLib;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Represents the current location of a file
    /// </summary>
    /// <author>Mark Forte</author>
    class FileLocation
    {
        private readonly string physicalName;
        public string PhysicalName
        {
            get { return physicalName; }
        }

        private readonly string path;
        public string Path        
        {
            get { return path; }
        }

        public FileLocation(string physicalName, string path)
        {
            this.physicalName = physicalName;
            this.path = path;
        }
    }
}