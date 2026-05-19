using System;
using System.Collections.Generic;
using System.Text;

namespace SisterBotService
{
    public class NotifierEmailData
    {

        public NotifierEmailData()
        {
                
        }
        internal string RecipientEmail { get; set; } = string.Empty;
        internal string SubjectEmail { get; set; } = string.Empty;
        internal string BodyEmail { get; set; } = string.Empty;
        internal string NotifyWhenAreElapsedInHours { get; set; } = string.Empty;
    }
}
