using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    /// <summary>
    /// Permisos específicos para la aplicación móvil, administrables por un Admin en tiempo real.
    /// Permiten anular (override) los permisos predeterminados del rol de un usuario.
    /// </summary>
    public class UserMobilePermission
    {
        [Key]
        public Guid UserId { get; set; }

        public bool CanManageLocations { get; set; }
        public bool CanCreateBudgets { get; set; }
        public bool CanManageClients { get; set; }
        public bool CanSeeReports { get; set; }
    }
}
