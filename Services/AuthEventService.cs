using Oracle.ManagedDataAccess.Client;
using FullSummpotAPI.Data;

namespace FullSummpotAPI.Services
{
    public class AuthEventService
    {
        private readonly OracleDbContext _db;

        public AuthEventService(OracleDbContext db) => _db = db;

        public void Log(OracleConnection conn, int? userId, string eventType, string? detail = null)
        {
            try
            {
                var cmd = new OracleCommand(@"
                    INSERT INTO AUTH_EVENTS (USER_ID, EVENT_TYPE, DETAIL)
                    VALUES (:userId, :eventType, :detail)", conn);
                cmd.BindByName = true;
                cmd.Parameters.Add("userId", OracleDbType.Int32).Value =
                    userId.HasValue ? userId.Value : (object)DBNull.Value;
                cmd.Parameters.Add("eventType", OracleDbType.Varchar2).Value = eventType;
                cmd.Parameters.Add("detail", OracleDbType.Varchar2).Value =
                    detail ?? (object)DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // AUTH_EVENTS table may not exist until migration is run — don't block auth
            }
        }
    }
}
