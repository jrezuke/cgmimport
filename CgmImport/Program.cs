using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using NLog;

namespace CgmImport
{
    class Program
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        //private static List<DbColumn> _dbColList = new List<DbColumn>(); 
        static void Main(string[] args)
        {
            if (args.Length > 0)
            {

            }
            Logger.Info("Starting CGM Import Service");

            var basePath = AppDomain.CurrentDomain.BaseDirectory;

            //get sites and load into list of siteInfo 
            var sites = GetSites();
            var skips = GetCgmSkips();
            //iterate sites
            foreach (var si in sites)
            {
                //TODO - comment this out
                //if (si.Id != 2)
                //    continue;
                Console.WriteLine("Site: " + si.Name);
                //get site randomized studies - return list of ChecksImportInfo
                var randList = GetRandimizedStudies(si.Id);

                //get the list of uploaded checks files in upload directory
                var cgmFileList = GetCgmFileInfos(si.Name);

                //iterate randomized list
                foreach (var subjectImportInfo in randList)
                {
                    //check for uploaded file
                    var fileInfo = cgmFileList.Find(x => x.SubjectId == subjectImportInfo.SubjectId);
                    if (fileInfo != null)
                        fileInfo.IsRandomized = true;

                    //if already imported then skip
                    if (subjectImportInfo.IsCgmImported)
                    {
                        Console.WriteLine("Subject already imported: " + subjectImportInfo.SubjectId);
                        continue;
                    }

                    //check if completed - if not then skip
                    if (!subjectImportInfo.SubjectCompleted)
                    {
                        Console.WriteLine("Subject not completed: " + subjectImportInfo.SubjectId);
                        continue;
                    }

                    if (fileInfo == null)
                    {
                        var emailNote = new EmailNotification { Message = "CGM file not uploaded." };
                        subjectImportInfo.EmailNotifications.Add(emailNote);
                        Console.WriteLine("Upload file not found: " + subjectImportInfo.SubjectId);
                        continue;
                    }

                    fileInfo.IsImportable = true;
                    Console.WriteLine("Subject is importable: " + subjectImportInfo.SubjectId);

                } //end of foreach (var subjectImportInfo in randList)

                //iterate file list
                //get list of files not on randomized list
                var notificationList = new List<EmailNotification>();
                foreach (var cgmFileInfo in cgmFileList)
                {
                    if (!cgmFileInfo.IsRandomized)
                    {
                        Console.WriteLine("CGM file is not randomized: " + cgmFileInfo.SubjectId);
                        notificationList.Add(new EmailNotification { Message = "CGM file is not randomized: ", SubjectId = cgmFileInfo.SubjectId });
                        continue;
                    }

                    if (cgmFileInfo.IsImportable)
                    {
                        if (!IsValidFile(cgmFileInfo))
                        {
                            Console.WriteLine("CGM file is not a valid format: " + cgmFileInfo.FileName);
                            notificationList.Add(new EmailNotification { Message = "CGM file is not a valid format: ", SubjectId = cgmFileInfo.SubjectId });
                            continue;
                        }
                        if (cgmFileInfo.SubjectId == "08-0160-5")
                        {
                            ;
                        }
                            
                        var dbRows = ParseFile(cgmFileInfo);
                        if (!(dbRows.Count > 0))
                        {
                            Console.WriteLine("CGM file has no rows: " + cgmFileInfo.FileName);
                            notificationList.Add(new EmailNotification { Message = "CGM file has no rows: ", SubjectId = cgmFileInfo.SubjectId });
                            continue;
                        }

                        var subjRandInfo = randList.Find(x => x.SubjectId == cgmFileInfo.SubjectId);
                        string message = string.Empty;
                        if (! skips.Contains(subjRandInfo.SubjectId))
                        {
                            if (!IsValidDateRange(dbRows, cgmFileInfo, subjRandInfo, out message))
                            {
                                notificationList.Add(new EmailNotification
                                {
                                    Message = message,
                                    SubjectId = cgmFileInfo.SubjectId
                                });
                                Console.WriteLine(message);
                                continue;
                            }
                        }
                        if (message.Length > 0)
                        {
                            Console.WriteLine(message);
                        }

                        if (ImportToDatabase(dbRows, subjRandInfo))
                        {
                            SetImportToCompleted(subjRandInfo.RandomizeId, subjRandInfo.SubjectId);
                            Logger.Info("Subject " + subjRandInfo.SubjectId + " was successfully imported.");
                            notificationList.Add(new EmailNotification { Message = "File was successfully imported", SubjectId = cgmFileInfo.SubjectId });

                        }
                        else
                        {
                            notificationList.Add(new EmailNotification { Message = "File import failed", SubjectId = cgmFileInfo.SubjectId });    
                        }
                        
                    }
                }//end foreach (var cgmFileInfo in cgmFileList)

                //send emails
                //Check to see if there are any notifications for this site
                var anyNotifications = randList.FindAll(x => x.EmailNotifications.Count > 0);
                if (anyNotifications.Count == 0 && notificationList.Count == 0)
                    continue;

                var toEmails = GetStaffForEvent(15, si.Id);
                SendEmailNotification(toEmails.ToArray(), anyNotifications, notificationList, basePath, si.Name);
                //Console.WriteLine("-----Email Notifications-----");
                //foreach (var subjectImportInfo in randList)
                //{
                //    var notifs = notificationList.FindAll(
                //            en => en.SubjectId == subjectImportInfo.SubjectId);

                //    if (subjectImportInfo.EmailNotifications.Count == 0 && notifs.Count == 0)
                //    {
                //        continue;
                //    }


                //    Console.WriteLine("Subject:" + subjectImportInfo.SubjectId);
                //    //Console.WriteLine("________________________________________");
                //    foreach (var emn in subjectImportInfo.EmailNotifications)
                //    {
                //        Console.WriteLine("    " + emn.Message);
                //    }


                //    if (notifs.Count > 0 )
                //    {
                //        foreach (var emailNotification in notifs)
                //        {
                //            emailNotification.IsNotified = true;
                //            Console.WriteLine("    " + emailNotification.Message);                      
                //        }

                //    }
                //}//end foreach (var subjectImportInfo in randList) send emails
            }//end foreach (var si in sites)


            //Console.Read();
        }

        private static HashSet<string> GetCgmSkips()
        {
            var hash = new HashSet<string>();
            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            using (var conn = new SqlConnection(strConn))
            {
                try
                {
                    var cmd = new SqlCommand("", conn)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandText = ("GetDexcomSkips")
                    };
                    conn.Open();
                    var rdr = cmd.ExecuteReader();

                    while (rdr.Read())
                    {
                        hash.Add(rdr.GetString(0));
                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }
            return hash;
        }
        private static void SendEmailNotification(string[] toEmails, List<SubjectImportInfo> subjectNotifs, List<EmailNotification> fileNotifs, string basePath, string siteName)
        {
            string subject = "CGM Import Exception Notifications for " + siteName;
            var sbBody = new StringBuilder("");
            const string newLine = "<br/>";

            sbBody.Append(newLine);
            sbBody.Append("<h2>CGM Import Exception Notifications</h2>");

            string notification = string.Empty;
            foreach (var subInfo in subjectNotifs)
            {
                var notifs = fileNotifs.FindAll(en => en.SubjectId == subInfo.SubjectId);
                if (subInfo.EmailNotifications.Count == 0 && notifs.Count == 0)
                    continue;

                sbBody.Append("<h4>" + "Subject:" + subInfo.SubjectId + "</h4>");
                sbBody.Append("<ul>");
                foreach (var emn in subInfo.EmailNotifications)
                {
                    sbBody.Append("<li>" + emn.Message + "</li>");
                }

                foreach (var emn in notifs)
                {
                    sbBody.Append("<li>" + emn.Message + "</li>");
                    emn.IsNotified = true;
                }

                sbBody.Append("</ul>");
            }
            foreach (var emn in fileNotifs)
            {
                if (emn.IsNotified)
                    continue;
                sbBody.Append("<h4>" + "Subject:" + emn.SubjectId + "</h4>");
                sbBody.Append("<ul>");
                sbBody.Append("<li>" + emn.Message + "</li>");
                sbBody.Append("</ul>");
            }

            SendHtmlEmail(subject, toEmails, null, sbBody.ToString(), basePath, siteName, "");

        }

        private static void SendHtmlEmail(string subject, string[] toAddress, IEnumerable<string> ccAddress,
            string bodyContent, string appPath, string siteName, string bodyHeader = "")
        {
            if (toAddress.Length == 0)
                return;

            var mm = new MailMessage { Subject = subject, Body = bodyContent };
            var path = Path.Combine(appPath, "mailLogo.jpg");
            var mailLogo = new LinkedResource(path);

            var sb = new StringBuilder("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.0 Transitional//EN\">");
            sb.Append("<html>");
            sb.Append("<head>");

            sb.Append("</head>");
            sb.Append("<body style='text-align:left;'>");
            sb.Append("<img style='width:200px;' alt='' hspace=0 src='cid:mailLogoID' align=baseline />");
            if (bodyHeader.Length > 0)
            {
                sb.Append(bodyHeader);
            }

            sb.Append("<div style='text-align:left;margin-left:30px;width:100%'>");
            sb.Append("<table style='margin-left:0px;'>");
            sb.Append(bodyContent);

            sb.Append("</table>");
            sb.Append("</div style='width:100px'>");

            sb.Append("</body>");
            sb.Append("</html>");

            AlternateView av = AlternateView.CreateAlternateViewFromString(sb.ToString(), null, "text/html");

            mailLogo.ContentId = "mailLogoID";
            av.LinkedResources.Add(mailLogo);

            mm.AlternateViews.Add(av);

            foreach (string s in toAddress)
                mm.To.Add(s);
            if (ccAddress != null)
            {
                foreach (string s in ccAddress)
                    mm.CC.Add(s);
            }

            Console.WriteLine("Send Email");
            Console.WriteLine("Subject:" + subject);
            Console.Write("To:" + toAddress[0]);
            //Console.Write("Email:" + sb);

            try
            {
                var smtp = new SmtpClient();
                smtp.Send(mm);
            }
            catch (Exception ex)
            {
                Logger.Info(ex.Message);
            }
        }

        private static List<string> GetStaffForEvent(int eventId, int siteId)
        {
            var emails = new List<string>();
            SqlDataReader rdr = null;
            var connStr = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            using (var conn = new SqlConnection(connStr))
            {
                try
                {
                    var cmd = new SqlCommand
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandText = "GetNotificationsStaffForEvent",
                        Connection = conn
                    };
                    var param = new SqlParameter("@eventId", eventId);
                    cmd.Parameters.Add(param);

                    conn.Open();
                    rdr = cmd.ExecuteReader();

                    while (rdr.Read())
                    {
                        int pos = rdr.GetOrdinal("AllSites");
                        var isAllSites = rdr.GetBoolean(pos);

                        pos = rdr.GetOrdinal("Email");
                        if (rdr.IsDBNull(pos))
                            continue;
                        var email = rdr.GetString(pos);

                        if (isAllSites)
                        {
                            emails.Add(email);
                            continue;
                        }

                        pos = rdr.GetOrdinal("SiteID");
                        var site = rdr.GetInt32(pos);

                        if (site == siteId)
                            emails.Add(email);

                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    if (rdr != null)
                        rdr.Close();
                }
            }

            return emails;
        }
        private static void SetImportToCompleted(int id, string subjectId)
        {
            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            using (var conn = new SqlConnection(strConn))
            {
                var cmd = new SqlCommand
                {
                    Connection = conn,
                    CommandText = "SetImportToCompleted",
                    CommandType = CommandType.StoredProcedure
                };
                var param = new SqlParameter("@id", id);
                cmd.Parameters.Add(param);
                try
                {
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    var sMsg = "Setting IsCgmImported = true failed: " + subjectId;
                    sMsg += ex.Message;
                    Logger.Error(sMsg, ex);
                }
            }
        }

        private static bool ImportToDatabase(IEnumerable<DbRow> dbRows, SubjectImportInfo subjRandInfo)
        {
            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            using (var conn = new SqlConnection(strConn))
            {
                conn.Open();
                using (SqlTransaction trn = conn.BeginTransaction())
                {
                    var row = 1;
                    foreach (var dbRow in dbRows)
                    {
                        var cmd = new SqlCommand
                        {
                            Transaction = trn,
                            Connection = conn,
                            CommandText = "AddDexcomRow",
                            CommandType = CommandType.StoredProcedure
                        };

                        var param = new SqlParameter("@studyId", subjRandInfo.StudyId);
                        cmd.Parameters.Add(param);
                        param = new SqlParameter("@subjectId", subjRandInfo.SubjectId);
                        cmd.Parameters.Add(param);
                        param = new SqlParameter("@siteId", subjRandInfo.SiteId);
                        cmd.Parameters.Add(param);

                        foreach (var col in dbRow.ColNameVals)
                        {
                            param = string.IsNullOrEmpty(col.Value)
                                ? new SqlParameter("@" + col.Name, DBNull.Value)
                                : new SqlParameter("@" + col.Name, col.Value);
                            cmd.Parameters.Add(param);
                        }
                        try
                        {
                            cmd.ExecuteNonQuery();
                        }
                        catch (Exception ex)
                        {
                            trn.Rollback();
                            var sMsg = "dexcom import error subject: " + subjRandInfo.SubjectId + ", row: " + row;
                            sMsg += ex.Message;
                            Logger.Error(sMsg, ex);
                            return false;
                        }
                        row++;
                    }
                    trn.Commit();
                }
                return true;
            }
        }

        private static bool IsValidDateRange(List<DbRow> dbRows, CgmFileInfo cgmFileInfo, SubjectImportInfo subjectImportInfo, out string message)
        {
            var firstCgmGlucoseDate = GetFirstCgmGlucoseDate(dbRows);
            if (firstCgmGlucoseDate == null)
            {
                message = "Invalid date range: Could not get the first glucose date from file";
                return false;
            }

            var lastCgmGlucoseDate = GetLastCgmGlucoseDate(dbRows);
            if (lastCgmGlucoseDate == null)
            {
                message = "Invalid date range: Could not get the last glucose date from file";
                return false;
            }

            //get checks first and last entries for subject
            GetFirstLastChecksSensorDates(cgmFileInfo, subjectImportInfo.StudyId);
            if ((cgmFileInfo.FirstChecksSensorDateTime == null || cgmFileInfo.LastChecksSensorDateTime == null))
            {
                message = "Invalid date range: Could not get checks first and last glucose entry dates:";
                return false;
            }


            if ((cgmFileInfo.FirstChecksSensorDateTime.Value.Date.CompareTo(firstCgmGlucoseDate.Value.Date) > 0))
            {
                message = "Invalid date range: The first sensor date(" + firstCgmGlucoseDate.Value.Date.ToShortDateString() +
                    ") is earlier than the first checks date(" +
                    cgmFileInfo.FirstChecksSensorDateTime.Value.ToShortDateString() +
                    ")";
                return false;
            }

            if ((cgmFileInfo.LastChecksSensorDateTime.Value.Date.CompareTo(lastCgmGlucoseDate.Value.Date) < 0))
            {
                message = "Invalid date range: The last sensor date(" + lastCgmGlucoseDate.Value.Date.ToShortDateString() +
                    ") is later than the last checks date(" +
                    cgmFileInfo.LastChecksSensorDateTime.Value.ToShortDateString() +
                    ")";
                return false;
            }
            message = "Valid date range:" + cgmFileInfo.SubjectId;
            return true;
        }

        private static DateTime? GetFirstCgmGlucoseDate(IEnumerable<DbRow> dbRows)
        {
            //try getting the date from the first row
            foreach (var dbRow in dbRows)
            {
                var datenv = dbRow.ColNameVals.Find(x => x.Name == "GlucoseDisplayTime");
                var date = GetDateFromNameValue(datenv.Value);
                if (date != null)
                    return date;

            }
            return null;
        }

        private static DateTime? GetLastCgmGlucoseDate(List<DbRow> dbRows)
        {
            //try getting the date from the first row
            for (var i = dbRows.Count - 1; i > 0; i--)
            {
                var dbRow = dbRows[i];
                var datenv = dbRow.ColNameVals.Find(x => x.Name == "GlucoseDisplayTime");
                var date = GetDateFromNameValue(datenv.Value);
                if (date != null)
                    return date;

            }
            return null;
        }
        private static DateTime? GetDateFromNameValue(string value)
        {
            DateTime date;
            if (DateTime.TryParse(value, out date))
                return date;
            return null;
        }
        private static List<DbRow> ParseFile(CgmFileInfo cgmFileInfo)
        {
            var dbRows = new List<DbRow>();
            using (var sr = new StreamReader(cgmFileInfo.FullName))
            {
                var rows = 0;
                string line;
                string[] colNameList = { };

                while ((line = sr.ReadLine()) != null)
                {
                    var columns = line.Split('\t');
                    if (string.IsNullOrEmpty(columns[2]))
                    {
                        break;
                    }

                    //first row contains the column names
                    if (rows == 0)
                    {
                        colNameList = (string[])columns.Clone();
                        rows++;
                        continue;
                    }

                    var dbRow = new DbRow();
                    //skip the first two columns - don't need - that's why i starts at 2
                    for (int i = 2; i < 13; i++)
                    {
                        
                        var col = columns[i];
                        if (col == "High")
                            col = "999";
                        if (col == "Low")
                            col = "0";

                        var colName = colNameList[i];

                        var colValName = new DbColNameVal { Name = colName, Value = col };

                        dbRow.ColNameVals.Add(colValName);
                    }
                    dbRows.Add(dbRow);
                }
            }
            return dbRows;
        }

        private static bool IsValidFile(CgmFileInfo cgmFileInfo)
        {
            var fullFileName = cgmFileInfo.FullName;
            using (var sr = new StreamReader(fullFileName))
            {
                if (sr.Peek() >= 0)
                {
                    //Reads the line, splits on tab and adds the components to the table
                    var line = sr.ReadLine();
                    if (line != null)
                    {
                        if (!line.Contains("PatientInfoField	PatientInfoValue"))
                        {
                            Console.WriteLine("***Invalid file: " + fullFileName);
                            Console.WriteLine(line);
                            return false;
                        }
                    }
                }
            }

            Console.WriteLine("Valid file: " + fullFileName);
            return true;
        }

        private static void GetFirstLastChecksSensorDates(CgmFileInfo cgmFileInfo, int studyId)
        {
            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            SqlDataReader rdr = null;
            using (var conn = new SqlConnection(strConn))
            {
                try
                {
                    var cmd = new SqlCommand("", conn) { CommandType = CommandType.StoredProcedure, CommandText = "GetChecksFirstAndLastSensorDateTime" };
                    var param = new SqlParameter("@studyId", studyId);
                    cmd.Parameters.Add(param);

                    conn.Open();
                    rdr = cmd.ExecuteReader();
                    if (rdr.Read())
                    {
                        var pos = rdr.GetOrdinal("firstDate");
                        if (!rdr.IsDBNull(pos))
                        {
                            cgmFileInfo.FirstChecksSensorDateTime = rdr.GetDateTime(pos);
                        }

                        pos = rdr.GetOrdinal("lastDate");
                        if (!rdr.IsDBNull(pos))
                        {
                            cgmFileInfo.LastChecksSensorDateTime = rdr.GetDateTime(pos);
                        }
                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    if (rdr != null)
                        rdr.Close();
                }
            }
        }

        private static IEnumerable<SiteInfo> GetSites()
        {
            var sil = new List<SiteInfo>();

            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            SqlDataReader rdr = null;
            using (var conn = new SqlConnection(strConn))
            {
                try
                {
                    var cmd = new SqlCommand("", conn) { CommandType = CommandType.StoredProcedure, CommandText = "GetSitesActive" };

                    conn.Open();
                    rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        var si = new SiteInfo();
                        var pos = rdr.GetOrdinal("ID");
                        si.Id = rdr.GetInt32(pos);

                        pos = rdr.GetOrdinal("Name");
                        si.Name = rdr.GetString(pos);

                        pos = rdr.GetOrdinal("SiteID");
                        si.SiteId = rdr.GetString(pos);

                        //pos = rdr.GetOrdinal("LastNovanetFileDateImported");
                        //si.LastFileDate = rdr.IsDBNull(pos) ? (DateTime?)null : rdr.GetDateTime(pos);

                        sil.Add(si);
                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    if (rdr != null)
                        rdr.Close();
                }
            }
            return sil;
        }

        private static List<SubjectImportInfo> GetRandimizedStudies(int site)
        {
            var list = new List<SubjectImportInfo>();

            String strConn = ConfigurationManager.ConnectionStrings["Halfpint"].ToString();
            SqlDataReader rdr = null;
            using (var conn = new SqlConnection(strConn))
            {
                try
                {
                    var cmd = new SqlCommand("", conn) { CommandType = CommandType.StoredProcedure, CommandText = "GetRandomizedStudiesForImportForSite" };

                    var param = new SqlParameter("@siteID", site);
                    cmd.Parameters.Add(param);

                    conn.Open();
                    rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        var ci = new SubjectImportInfo { SiteId = site };

                        var pos = rdr.GetOrdinal("ID");
                        ci.RandomizeId = rdr.GetInt32(pos);

                        pos = rdr.GetOrdinal("SubjectId");
                        ci.SubjectId = rdr.GetString(pos).Trim();

                        pos = rdr.GetOrdinal("StudyId");
                        ci.StudyId = rdr.GetInt32(pos);

                        pos = rdr.GetOrdinal("Arm");
                        ci.Arm = rdr.GetString(pos);

                        pos = rdr.GetOrdinal("IsCgmImported");
                        ci.IsCgmImported = !rdr.IsDBNull(pos) && rdr.GetBoolean(pos);

                        pos = rdr.GetOrdinal("ChecksLastRowImported");
                        ci.LastRowImported = !rdr.IsDBNull(pos) ? rdr.GetInt32(pos) : 0;

                        pos = rdr.GetOrdinal("DateCompleted");
                        if (!rdr.IsDBNull(pos))
                        {
                            ci.DateCompleted = rdr.GetDateTime(pos);
                            ci.SubjectCompleted = true;
                        }

                        pos = rdr.GetOrdinal("DateRandomized");
                        if (!rdr.IsDBNull(pos))
                            ci.DateRandomized = rdr.GetDateTime(pos);

                        pos = rdr.GetOrdinal("SiteName");
                        ci.SiteName = rdr.GetString(pos);

                        list.Add(ci);
                    }
                    rdr.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    if (rdr != null)
                        rdr.Close();
                }
            }

            return list;
        }

        private static List<CgmFileInfo> GetCgmFileInfos(string siteName)
        {
            var list = new List<CgmFileInfo>();

            var folderPath = ConfigurationManager.AppSettings["CgmUploadPath"];
            var path = Path.Combine(folderPath, siteName);

            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);

                FileInfo[] fis = di.GetFiles();

                list.AddRange(fis.OrderBy(f => f.Name).Select(fi => new CgmFileInfo
                {
                    FileName = fi.Name,
                    FullName = fi.FullName,
                    SubjectId = fi.Name.Replace("_CGM.csv", ""),
                    IsRandomized = false
                }));
            }
            return list;
        }
    }

    public class SiteInfo
    {
        public int Id { get; set; }
        public string SiteId { get; set; }
        public string Name { get; set; }

    }

    public class CgmFileInfo
    {
        public string FileName { get; set; }
        public string FullName { get; set; }
        public string SubjectId { get; set; }

        public bool IsRandomized { get; set; }
        public bool IsValidFile { get; set; }
        public string InvalidReason { get; set; }
        public bool IsImportable { get; set; }
        public DateTime? FirstChecksSensorDateTime { get; set; }
        public DateTime? LastChecksSensorDateTime { get; set; }
    }

    public class SubjectImportInfo
    {
        public SubjectImportInfo()
        {
            EmailNotifications = new List<EmailNotification>();
        }
        public int RandomizeId { get; set; }
        public string Arm { get; set; }
        public string SubjectId { get; set; }
        public int SiteId { get; set; }
        public string SiteName { get; set; }
        public int StudyId { get; set; }
        public bool SubjectCompleted { get; set; }
        public bool IsCgmImported { get; set; }
        public int RowsCompleted { get; set; }
        public int LastRowImported { get; set; }
        public DateTime? DateRandomized { get; set; }
        public DateTime? DateCompleted { get; set; }

        public List<EmailNotification> EmailNotifications { get; set; }

    }

    public class EmailNotification
    {
        public string SubjectId { get; set; }
        public string Message { get; set; }
        public bool IsNotified { get; set; }

    }

    public class DbRow
    {
        public DbRow()
        {
            ColNameVals = new List<DbColNameVal>();
        }
        public List<DbColNameVal> ColNameVals { get; set; }
    }

    public class DbColNameVal
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

}
