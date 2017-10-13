using System;
using System.Collections;
using System.Collections.Specialized;
using Meebey.SmartIrc4net;
using System.Threading;
using System.Text.RegularExpressions;
using log4net;

namespace CVNBot
{
    struct RCEvent
    {
        public enum EventType
        {
            delete, restore, upload, block, unblock, edit, protect, unprotect,
            move, rollback, newuser, import, unknown, newuser2, autocreate,
            modifyprotect
        }

        public string project;
        public string title;
        public string url;
        public string user;
        public bool minor;
        public bool newpage;
        public bool botflag;
        public int szdiff;
        public string comment;
        public EventType eventtype;
        public string blockLength;
        public string movedTo;

        public override string ToString()
        {
            return "[" + project + "] " + user + " edited [[" + title + "]] (" + szdiff.ToString() + ") " + url + " " + comment;
        }
    }

    class RCReader
    {
        public IrcClient rcirc = new IrcClient();
        public DateTime lastMessage = DateTime.Now;

        // RC parsing regexen
        static Regex stripColours = new Regex(@"\x04\d{0,2}\*?");
        static Regex stripColours2 = new Regex(@"\x03\d{0,2}");
        static Regex stripBold = new Regex(@"\x02");
        static Regex rszDiff = new Regex(@"\(([\+\-])([0-9]+)\)");

        private static ILog logger = LogManager.GetLogger("CVNBot.RCReader");

        public void initiateConnection()
        {
            Thread.CurrentThread.Name = "RCReader";

            logger.Info("RCReader thread started");

            // Set up RCReader
            rcirc.Encoding = System.Text.Encoding.UTF8;
            rcirc.AutoReconnect = true;
            rcirc.AutoRejoin = true;

            rcirc.OnChannelMessage += new IrcEventHandler(rcirc_OnChannelMessage);
            rcirc.OnConnected += new EventHandler(rcirc_OnConnected);

            try
            {
                rcirc.Connect("irc.wikimedia.org", 6667);
            }
            catch (ConnectionException e)
            {
                logger.Warn( "Connection error: " + e.Message );
                return;
            }

            try
            {
                rcirc.Login(Program.botNick, "CVNBot", 4, "CVNBot");

                foreach (string prj in Program.prjlist.Keys)
                {
                    //logger.Info("Joining #" + prj);
                    rcirc.RfcJoin("#" + prj);
                }

                // Enter loop
                rcirc.Listen();
                // when Listen() returns the IRC session is over
                rcirc.Disconnect();
            }
            catch (ConnectionException)
            {
                // Final disconnect may throw, ignore.
                return;
            }
        }

        void rcirc_OnConnected(object sender, EventArgs e)
        {
            logger.Info("Connected to RC feed");
        }

        void rcirc_OnChannelMessage(object sender, IrcEventArgs e)
        {
            lastMessage = DateTime.Now;

            // Based on RCParser.py->parseRCmsg()
            // Example message from 2017-10-13 from #en.wikipedia
            // 01> #00314 [[
            // 02> #00307 Special:Log/newusers
            // 03> #00314 ]]
            // 04> #0034   create2
            // 05> #00310
            // 06> #00302
            // 07> #003
            // 08> #0035  *
            // 09> #003
            // 10> #00303 Ujju.19788
            // 11> #003
            // 12> #0035  *
            // 13> #003
            // 14> #00310 created new account User:Upendhare
            // 15> #003
            string strippedmsg = stripBold.Replace(stripColours.Replace(CVNBotUtils.replaceStrMax(e.Data.Message, '\x03', '\x04', 14), "\x03"), "");
            string[] fields = strippedmsg.Split(new char[1] { '\x03' }, 15);
            if (fields.Length == 15)
            {
                if (fields[14].EndsWith("\x03"))
                    fields[14] = fields[14].Substring(0, fields[14].Length - 1);
            }
            else
            {
                // Probably really long article title or something that got cut off; we can't handle these
                return;
            }

            try
            {
                RCEvent rce;
                rce.eventtype = RCEvent.EventType.unknown;
                rce.blockLength = "";
                rce.movedTo = "";
                rce.project = e.Data.Channel.Substring(1);
                rce.title = Project.translateNamespace(rce.project, fields[2]);
                rce.url = CVNBotUtils.rootUrl(fields[6]);
                rce.user = fields[10];
                //At the moment, fields[14] contains IRC colour codes. For plain edits, remove just the \x03's. For logs, remove using the regex.
                Match titlemo = ((Project)Program.prjlist[rce.project]).rSpecialLogRegex.Match(fields[2]);
                if (!titlemo.Success)
                {
                    //This is a regular edit
                    rce.minor = fields[4].Contains("M");
                    rce.newpage = fields[4].Contains("N");
                    rce.botflag = fields[4].Contains("B");
                    //logger.Info("DEBUG: fields[4]:" + fields[4]);
                    rce.eventtype = RCEvent.EventType.edit;
                    rce.comment = fields[14].Replace("\x03", "");
                }
                else
                {
                    //This is a log edit; check for type
                    string logType = titlemo.Groups[1].Captures[0].Value;
                    //Fix comments
                    rce.comment = stripColours2.Replace(fields[14], "");
                    switch (logType)
                    {
                        case "newusers":
                            // Could be a user creating their own account, or a user creating a sockpuppet

                            // Example message as of 2016-11-02 on #nl.wikipedia (with log comment after colon)
                            // > [[Speciaal:Log/newusers]] create2  * BRPots *  created new account Gebruiker:BRPwiki: eerder fout gemaakt
                            // Example message as of 2016-11-02 on #nl.wikipedia (without log comment)
                            // > [[Speciaal:Log/newusers]] create2  * Sherani koster *  created new account Gebruiker:Rani farah koster

                            // Example message as of 2017-10-13 on #en.wikipedia:
                            // > [[Special:Log/newusers]] create2  * Ujju.19788 *  created new account User:Upendhare
                            if (fields[4].Contains("create2"))
                            {
                                Match mc2 = ((Project)Program.prjlist[rce.project]).rCreate2Regex.Match(rce.comment);
                                if (mc2.Success)
                                {
                                    rce.title = mc2.Groups[1].Captures[0].Value;
                                    rce.eventtype = RCEvent.EventType.newuser2;
                                }
                                else
                                {
                                    logger.Warn("Unmatched create2 event in " + rce.project + ": " + e.Data.Message);
                                }
                            }
                            else
                            {
                                if(fields[4].Contains("autocreate"))
                                {
                                    rce.eventtype = RCEvent.EventType.autocreate;
                                }
                                else
                                {
                                    rce.eventtype = RCEvent.EventType.newuser;
                                }
                            }
                            break;
                        case "block":
                            try
                            {
                                //Could be a block or unblock; need to parse regex
                                Match bm = ((Project)Program.prjlist[rce.project]).rblockRegex.Match(rce.comment);
                                if (bm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.block;
                                    rce.title = bm.Groups["item1"].Captures[0].Value;
                                    rce.blockLength = "24 hours"; //Set default value in case our Regex has fallen back to laziness
                                    try
                                    {
                                        rce.blockLength = bm.Groups["item2"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                    try
                                    {
                                        rce.comment = bm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    Match ubm = ((Project)Program.prjlist[rce.project]).runblockRegex.Match(rce.comment);
                                    if (ubm.Success)
                                    {
                                        rce.eventtype = RCEvent.EventType.unblock;
                                        rce.title = ubm.Groups["item1"].Captures[0].Value;
                                        try
                                        {
                                            rce.comment = ubm.Groups["comment"].Captures[0].Value;
                                        }
                                        catch (ArgumentOutOfRangeException) { }
                                    }
                                    else
                                    {
                                        //All failed; is block but regex does not match
                                        logger.Warn("Unmatched block type in " + rce.project + ": " + e.Data.Message);
                                        return;
                                    }
                                }
                            }
                            catch (ArgumentOutOfRangeException aoore)
                            {
                                logger.Error("Failed to handle RCEvent.log.block", aoore);
                                Program.BroadcastDD("ERROR", "RCR_AOORE_2", aoore.Message, e.Data.Channel + "/" + e.Data.Message);
                            }
                            break;
                        case "protect":
                            //Could be a protect, modifyprotect or unprotect; need to parse regex
                            Match pm = ((Project)Program.prjlist[rce.project]).rprotectRegex.Match(rce.comment);
                            Match modpm = ((Project)Program.prjlist[rce.project]).rmodifyprotectRegex.Match(rce.comment);
                            Match upm = ((Project)Program.prjlist[rce.project]).runprotectRegex.Match(rce.comment);
                            if (pm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.protect;
                                rce.title = Project.translateNamespace(rce.project, pm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = pm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else if (modpm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.modifyprotect;
                                rce.title = Project.translateNamespace(rce.project, modpm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = modpm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                if (upm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.unprotect;
                                    rce.title = Project.translateNamespace(rce.project, upm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = upm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched protect type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            break;
                        case "rights":
                            //Is rights
                            return; //Not interested today
                        //break;
                        case "delete":
                            //Could be a delete or restore; need to parse regex
                            //_1568: ADDED: Support for deletions, now reported in rc stream
                            Match dm = ((Project)Program.prjlist[rce.project]).rdeleteRegex.Match(rce.comment);
                            if (dm.Success)
                            {
                                rce.eventtype = RCEvent.EventType.delete;
                                rce.title = Project.translateNamespace(rce.project, dm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = dm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match udm = ((Project)Program.prjlist[rce.project]).rrestoreRegex.Match(rce.comment);
                                if (udm.Success)
                                {
                                    rce.eventtype = RCEvent.EventType.restore;
                                    rce.title = Project.translateNamespace(rce.project, udm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = udm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    // could be 'revision' (change visibility of revision) or something else
                                    // ignore for now, not supported not interested
                                    //logger.Warn("Unmatched delete type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            break;
                        case "upload":
                            //Is an upload
                            Match um = ((Project)Program.prjlist[rce.project]).ruploadRegex.Match(rce.comment);
                            if (um.Success)
                            {
                                rce.eventtype = RCEvent.EventType.upload;
                                rce.title = Project.translateNamespace(rce.project, um.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = um.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                            	// could be 'overwrite' (upload new version) or something else
                                // ignore for now, not supported not interested
                                //logger.Warn("Unmatched upload in " + rce.project + ": " + e.Data.Message);
                                return;
                            }
                            break;
                        case "move":
                            //Is a move
                            rce.eventtype = RCEvent.EventType.move;
                            //Check "move over redirect" first: it's longer, and plain "move" may match both (e.g., en-default)
                            Match mrm = ((Project)Program.prjlist[rce.project]).rmoveredirRegex.Match(rce.comment);
                            if (mrm.Success)
                            {
                                rce.title = Project.translateNamespace(rce.project, mrm.Groups["item1"].Captures[0].Value);
                                rce.movedTo = Project.translateNamespace(rce.project, mrm.Groups["item2"].Captures[0].Value);
                                //We use the unused blockLength field to store our "moved from" URL
                                rce.blockLength = CVNBotUtils.rootUrl(((Project)Program.prjlist[rce.project]).rooturl) + "wiki/" + CVNBotUtils.wikiEncode(mrm.Groups["item1"].Captures[0].Value);
                                try
                                {
                                    rce.comment = mrm.Groups["comment"].Captures[0].Value;
                                }
                                catch (ArgumentOutOfRangeException) { }
                            }
                            else
                            {
                                Match mm = ((Project)Program.prjlist[rce.project]).rmoveRegex.Match(rce.comment);
                                if (mm.Success)
                                {
                                    rce.title = Project.translateNamespace(rce.project, mm.Groups["item1"].Captures[0].Value);
                                    rce.movedTo = Project.translateNamespace(rce.project, mm.Groups["item2"].Captures[0].Value);
                                    //We use the unused blockLength field to store our "moved from" URL
                                    rce.blockLength = CVNBotUtils.rootUrl(((Project)Program.prjlist[rce.project]).rooturl) + "wiki/" + CVNBotUtils.wikiEncode(mm.Groups["item1"].Captures[0].Value);
                                    try
                                    {
                                        rce.comment = mm.Groups["comment"].Captures[0].Value;
                                    }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                else
                                {
                                    logger.Warn("Unmatched move type in " + rce.project + ": " + e.Data.Message);
                                    return;
                                }
                            }
                            break;
                        case "import":
                            //Is an import
                            //rce.eventtype = RCEvent.EventType.import;
                            return; //Not interested today
                        //break;
                        default:
                            //logger.Warn("Unhandled log type: " + logType + " in " + rce.project + ": " + e.Data.Message);
                            //Don't react to event
                            return;
                    }
                    //These flags don't apply to log events, but must be initialized
                    rce.minor = false;
                    rce.newpage = false;
                    rce.botflag = false;
                }

                //Deal with the diff size
                Match n = rszDiff.Match(fields[13]);
                if (n.Success)
                {
                    if (n.Groups[1].Captures[0].Value == "+")
                        rce.szdiff = Convert.ToInt32(n.Groups[2].Captures[0].Value);
                    else
                        rce.szdiff = 0 - Convert.ToInt32(n.Groups[2].Captures[0].Value);
                }
                else
                    rce.szdiff = 0;

                try
                {
                    Program.ReactToRCEvent(rce);
                }
                catch (Exception exce)
                {
                    logger.Error("Failed to handle RCEvent", exce);
                    Program.BroadcastDD("ERROR", "ReactorException", exce.Message, e.Data.Channel + " " + e.Data.Message);
                }
            }
            catch (ArgumentOutOfRangeException eor)
            {
                // Broadcast this for Distributed Debugging
                logger.Error("Failed to process incoming message", eor);
                Program.BroadcastDD("ERROR", "RCR_AOORE", eor.Message, e.Data.Channel + "/" + e.Data.Message
                    + "Fields: " + fields.ToString());
            }
        }

    }
}
