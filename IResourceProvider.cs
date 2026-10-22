using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModemAPI
{
    /// <summary>
    /// Gives <see cref="LMDTPServer"/> the resources it serves. The server calls these methods for every request, from a thread pool
    /// thread, possibly for several requests at the same time, so an implementation must be thread-safe. All of them must describe
    /// the same data: the size, the hashes and the stream of a resource must match each other, otherwise the client rejects it.
    /// </summary>
    public interface IResourceProvider
    {
        /// <summary>
        /// Opens the data of a resource. Called once per OBTAIN request, after the other methods.
        /// </summary>
        /// <param name="resourceName">Resource name, as the client sent it (UTF-8, up to 65535 bytes)</param>
        /// <returns>A new readable stream positioned at the first byte of the resource, with at least <see cref="GetResourceSize"/>
        /// bytes left in it (the server sends exactly that many and ignores the rest), or null if it can't be opened. It doesn't have to
        /// support seeking. The server disposes it when the request is over.</returns>
        Stream? GetResource(string resourceName);
        /// <summary>
        /// Gets the length of a resource in bytes: 0 or more (a negative size makes the server answer with a server-side error).
        /// A resource longer than the client agreed to download is refused with <see cref="LMDTPResponseFlags.LargeMessageExceededMTU"/>.
        /// </summary>
        long GetResourceSize(string resourceName);
        /// <summary>
        /// Gets SHA-512 of exactly <see cref="GetResourceSize"/> bytes of the resource: 64 bytes, anything else makes the server answer
        /// with a server-side error. Used by every client that doesn't take XxHash3, and for HASH requests.
        /// Computing it reads the whole resource, so a provider should compute it once and keep it. See <see cref="LMDTPHashAlgorithms"/> for what it guarantees.
        /// </summary>
        byte[] GetResourceSha512Hashsum(string resourceName);
        /// <summary>
        /// Gets the hash of a resource computed with the given algorithm, or null, if this provider can't give it.
        /// When a client asks for a faster hash and this returns null, the server falls back to <see cref="GetResourceSha512Hashsum"/>.
        /// <see cref="LMDTPServer.ComputeResourceHash"/> computes any supported hash of a stream.
        /// </summary>
        /// <param name="resourceName">Resource name</param>
        /// <param name="algorithm">One of the algorithms</param>
        /// <returns>The hash (8 bytes for XxHash3, 64 bytes for SHA-512) or null; an XxHash3 of another length is ignored</returns>
        byte[]? GetResourceHash(string resourceName, LMDTPHashAlgorithms algorithm)
        {
            return algorithm == LMDTPHashAlgorithms.Sha512 ? GetResourceSha512Hashsum(resourceName) : null;
        }
        /// <summary>
        /// Gets the flags of the response. Called first: if it returns any of the error flags (0x0001 to 0x0010), the client gets
        /// only the header with these flags and fails with the matching exception, the other methods aren't called.
        /// <see cref="LMDTPResponseFlags.Success"/> lets the request go on. <see cref="LMDTPResponseFlags.HashIsXxHash3"/> is set by the server itself.
        /// </summary>
        LMDTPResponseFlags GetResourceResponseFlags(string resourceName);
    }
}
