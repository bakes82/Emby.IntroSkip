using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace IntroSkip.Database.Models
{
    public class FileFingerprint : BaseItem
    {
        public List<uint> Fingerprint { get; set; }
    }
}