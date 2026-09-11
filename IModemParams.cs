using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public interface IModemParams
    {
        void SetModemParams(params object[] settings);
        object[] GetModemParams();
    }
}
