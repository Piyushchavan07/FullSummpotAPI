using Npgsql;
using FullSummpotAPI.Data;

namespace FullSummpotAPI.Services
{
    public class AuthEventService
    {
        private readonly NpgsqlDbContext _db;

        public AuthEventService(NpgsqlDbContext db) => _db = db;

        public void Log(NpgsqlConnection conn, int? userId, string eventType, string? detail = null)
        {
            try
            {
                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO auth_events (user_id, event_type, detail)
                    VALUES (@userId, @eventType, @detail)", conn);
                cmd.Parameters.AddWithValue("userId", userId.HasValue ? (object)userId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("eventType", eventType);
                cmd.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // auth_events table may not exist — don't block auth
            }
        }
    }
}
