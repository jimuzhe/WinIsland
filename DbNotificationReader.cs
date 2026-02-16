using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace WinIsland
{
    public class NotificationItem
    {
        public string Title { get; set; }
        public string Body { get; set; }
        public string AppName { get; set; }
        public string AppId { get; set; } // PrimaryId / AUMID
        public DateTime ArrivalTime { get; set; }
        public long ArrivalTimeTicks { get; set; }
    }

    public class DbNotificationReader
    {
        private string _dbPath;
        private DateTime _lastCheckTime;
        private long _lastCheckTicks;

        public event EventHandler<NotificationItem> NotificationReceived;

        public DbNotificationReader()
        {
            _dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                "Microsoft", "Windows", "Notifications", "wpndatabase.db");
            
            // Start checking from now to avoid flooding with old notifications
            _lastCheckTime = DateTime.Now.AddSeconds(-5); // Just a small buffer
            _lastCheckTicks = _lastCheckTime.ToFileTime();
        }

        public async Task CheckForNewNotificationsAsync()
        {
            try
            {
                if (!File.Exists(_dbPath)) return;

                // Create a temp copy to avoid locking issues
                string tempDbPath = Path.Combine(Path.GetTempPath(), $"wpndatabase_copy_{Guid.NewGuid()}.db");
                
                // Copy wal/shm if they exist
                string dbWal = _dbPath + "-wal";
                string dbShm = _dbPath + "-shm";
                string tempWal = tempDbPath + "-wal";
                string tempShm = tempDbPath + "-shm";

                try
                {
                    File.Copy(_dbPath, tempDbPath, true);
                    if (File.Exists(dbWal)) File.Copy(dbWal, tempWal, true);
                    if (File.Exists(dbShm)) File.Copy(dbShm, tempShm, true);
                }
                catch (IOException)
                {
                    // File might be locked or busy, skip this cycle
                    return;
                }

                List<NotificationItem> newItems = new List<NotificationItem>();

                using (var connection = new SqliteConnection($"Data Source={tempDbPath}"))
                {
                    await connection.OpenAsync();

                    // Query for new toast notifications
                    var command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT n.Payload, n.ArrivalTime, h.PrimaryId 
                        FROM Notification n 
                        JOIN NotificationHandler h ON n.HandlerId = h.RecordId 
                        WHERE n.Type = 'toast' AND n.ArrivalTime > $lastCheck 
                        ORDER BY n.ArrivalTime ASC";
                    
                    command.Parameters.AddWithValue("$lastCheck", _lastCheckTicks);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            try
                            {
                                long arrivalTimeTicks = reader.GetInt64(1);
                                string appId = reader.GetString(2);
                                byte[] payload = (byte[])reader["Payload"];
                                string xmlContent = Encoding.UTF8.GetString(payload);
                                
                                var item = ParseNotificationXml(xmlContent);
                                if (item != null)
                                {
                                    item.AppId = appId;
                                    item.AppName = item.AppName ?? appId; // Fallback
                                    item.ArrivalTimeTicks = arrivalTimeTicks;
                                    item.ArrivalTime = DateTime.FromFileTime(arrivalTimeTicks);
                                    newItems.Add(item);
                                }

                                if (arrivalTimeTicks > _lastCheckTicks)
                                {
                                    _lastCheckTicks = arrivalTimeTicks;
                                }
                            }
                            catch { }
                        }
                    }
                }

                // Cleanup temp files
                try
                {
                    if (File.Exists(tempDbPath)) File.Delete(tempDbPath);
                    if (File.Exists(tempWal)) File.Delete(tempWal);
                    if (File.Exists(tempShm)) File.Delete(tempShm);
                }
                catch { }

                // Fire events
                foreach (var item in newItems)
                {
                    NotificationReceived?.Invoke(this, item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DbNotificationReader Error: {ex.Message}");
            }
        }

        private NotificationItem ParseNotificationXml(string xml)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xml)) return null;

                // Xml usually starts with <toast>...
                // Only parse if it looks cleaner
                int idx = xml.IndexOf("<toast");
                if (idx == -1) return null;
                
                string cleanXml = xml.Substring(idx);
                // Remove trailing garbage if any (though usually clean in blob)
                // Sometimes blobs have null bytes at end? Sqlite blob usually exact.

                XDocument doc = XDocument.Parse(cleanXml);
                
                // Extract text elements from visual binding
                var binding = doc.Descendants("binding").FirstOrDefault(x => x.Attribute("template")?.Value == "ToastGeneric");
                if (binding == null) return null;

                var texts = binding.Descendants("text").ToList();
                
                string title = "";
                string body = "";

                if (texts.Count > 0) title = texts[0].Value;
                if (texts.Count > 1) body = texts[1].Value;

                // Try to find AppName override?
                // Sometimes <toast launch="..." displayTimestamp="..."> attributes exist
                // Often Title is the AppName if explicit, but usually Title is "Sender Name" or "Subject".
                // The AppName is usually not in the XML payload itself visually unless it's the first text line.
                // However, often the first text IS the title of the notification (e.g. "Discord"), and second is content.

                return new NotificationItem
                {
                    Title = title,
                    Body = body
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
