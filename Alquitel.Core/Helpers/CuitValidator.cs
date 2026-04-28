namespace Alquitel.Core.Helpers
{
    public static class CuitValidator
    {
        public static bool IsValid(string? cuit)
        {
            if (string.IsNullOrWhiteSpace(cuit)) return false;
            
            cuit = cuit.Replace("-", "").Replace(" ", "");
            if (cuit.Length != 11) return false;
            if (!long.TryParse(cuit, out _)) return false;

            int[] multipliers = { 5, 4, 3, 2, 7, 6, 5, 4, 3, 2 };
            int total = 0;
            
            for (int i = 0; i < 10; i++)
            {
                total += int.Parse(cuit[i].ToString()) * multipliers[i];
            }

            int rest = total % 11;
            int verificationCode = rest == 0 ? 0 : (rest == 1 ? 9 : 11 - rest);

            return verificationCode == int.Parse(cuit[10].ToString());
        }
    }
}
