using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yvand.LdapClaimsProvider.Utility
{
    internal static class ListUtility
    {
        internal static List<string> StringToList(string value)
        {
            return value.Split(';').Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        }
    }
}
