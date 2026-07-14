using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    /// <summary>Rol de aplicación. Gobierna qué secciones de la UI puede usar cada empleado.</summary>
    public enum UserRole
    {
        /// <summary>Acceso total: catálogo, configuración, reportes y gestión de usuarios.</summary>
        Admin = 0,
        /// <summary>Solo operación comercial: presupuestos, clientes y ubicaciones.</summary>
        Vendedor = 1,
        /// <summary>
        /// Personal de depósito que arma los equipos en eventos. Interfaz mínima:
        /// solo ve y previsualiza las Órdenes de Trabajo (OT) que se le envían.
        /// </summary>
        Armador = 2
    }

    /// <summary>
    /// Usuario del sistema. Con la base de datos compartida (Supabase/PostgreSQL) todos
    /// los puestos de trabajo ven la misma lista de usuarios y cada orden registra quién
    /// la creó (<see cref="Order.CreatedByUserId"/>).
    /// </summary>
    public class User
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string Name { get; set; } = string.Empty;

        public UserRole Role { get; set; } = UserRole.Vendedor;

        /// <summary>
        /// Hash PBKDF2 con formato "iteraciones.saltBase64.hashBase64", generado por
        /// <see cref="Helpers.PasswordHasher"/>. Null o vacío = usuario sin contraseña
        /// (entra con solo elegir su nombre).
        /// </summary>
        public string? PasswordHash { get; set; }

        /// <summary>Borrado lógico, mismo patrón que Client/Product.</summary>
        public bool IsArchived { get; set; } = false;

        /// <summary>Los Pickers de MAUI muestran ToString(); WPF usa DisplayMemberPath.</summary>
        public override string ToString() => Name;
    }
}
