using Microsoft.AspNetCore.Mvc;
using Oracle.ManagedDataAccess.Client;
using FullSummpotAPI.Data;

namespace FullSummpotAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly OracleDbContext _db;

        public UserController(OracleDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public IActionResult GetUsers()
        {
            var users = new List<object>();

            try
            {
                using var conn = _db.GetConnection();
                conn.Open();
string query = "SELECT USER_ID, USERNAME, EMAIL FROM USERS";

                using var cmd = new OracleCommand(query, conn);
                using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    users.Add(new
    {
        UserId = Convert.ToInt32(reader["USER_ID"]),
        Username = reader["USERNAME"].ToString(),
        Email = reader["EMAIL"].ToString()
    });
}
                return Ok(users);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
    }
}