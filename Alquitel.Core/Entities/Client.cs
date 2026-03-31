using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    public class Client
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string CompanyName { get; set; } = string.Empty;
        [Required]
        public string Cuit { get; set; } = string.Empty;
        public string? ContactName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
    }
}
