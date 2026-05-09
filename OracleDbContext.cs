using Oracle.ManagedDataAccess.Client;

namespace FullSummpotAPI.Data
{
    public class OracleDbContext
    {
        private readonly string _connectionString;

        public OracleDbContext(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("OracleDb")
                ?? throw new InvalidOperationException("Connection string 'OracleDb' not found.");
        }

        public OracleConnection GetConnection()
        {
            return new OracleConnection(_connectionString);
        }
    }
}