using Npgsql;

namespace FullSummpotAPI.Data
{
    public class NpgsqlDbContext
    {
        private readonly string _connectionString;

        public NpgsqlDbContext(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("NeonDb")
                ?? throw new InvalidOperationException("Connection string 'NeonDb' not found.");
        }

        public NpgsqlConnection GetConnection()
        {
            return new NpgsqlConnection(_connectionString);
        }
    }
}
