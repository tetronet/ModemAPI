using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public interface IResourceProvider
    {
        Stream GetResource(string resourceName);
        long GetResourceSize(string resourceName);
        byte[] GetResourceSha512Hashsum(string resourceName);
    }
}
