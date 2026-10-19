using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    public interface IResourceProvider
    {
        Stream? GetResource(string resourceName);
        long GetResourceSize(string resourceName);
        byte[] GetResourceSha512Hashsum(string resourceName);
        /// <summary>
        /// Gets the hash of a resource computed with the given algorithm, or null, if this provider can't give it.
        /// When a client asks for a faster hash and this returns null, the server falls back to <see cref="GetResourceSha512Hashsum"/>.
        /// <see cref="LMDTPServer.ComputeResourceHash"/> computes any supported hash of a stream.
        /// </summary>
        /// <param name="resourceName">Resource name</param>
        /// <param name="algorithm">One of the algorithms</param>
        /// <returns>The hash (8 bytes for XxHash3, 64 bytes for SHA-512) or null</returns>
        byte[]? GetResourceHash(string resourceName, LMDTPHashAlgorithms algorithm)
        {
            return algorithm == LMDTPHashAlgorithms.Sha512 ? GetResourceSha512Hashsum(resourceName) : null;
        }
        LMDTPResponseFlags GetResourceResponseFlags(string resourceName);
    }
}
