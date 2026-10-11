using System.Numerics;

namespace ModemAPI
{
    public class Address : IEquatable<Address>
    {
        /// <summary>
        /// Stores the actual address.
        /// </summary>
        public string? AddressValue { get; set; }
        public int Length { get { return AddressValue?.Length ?? 0; } }
        public Address()
        {

        }
        public Address(string addressValue)
        {
            AddressValue = addressValue;
        }

        public bool Equals(Address? other)
        {
            try
            {
                return AddressValue == other?.AddressValue;
            }
            catch
            {
                return false;
            }
        }
        /// <summary>
        /// Checks if an address is a valid tetronet address expression.
        /// </summary>
        /// <param name="address">Address to check</param>
        /// <returns><see langword="true"/> if the address is a valid tetronet address expression and <see langword="false"/> if not.</returns>
        public static bool Validate(Address? address)
        {
            return address != null &&
                address.AddressValue != null &&
                address.Length > 0 &&
                address.Length <= 255 &&
                address.AddressValue.All(delegate (char c) { return !PrintableCharacterList.IsCharacterWrongAddress(c); }) &&
                !address.AddressValue.StartsWith('-') &&
                !address.AddressValue.EndsWith('-') &&
                !address.AddressValue.Contains("--");
        }
        /// <summary>
        /// Creates a one-to-one copy of the address object.
        /// </summary>
        /// <param name="original">The original address</param>
        /// <returns>The copied address</returns>
        public static Address Copy(Address? original)
        {
            if (original == null)
            {
                return new Address();
            }
            return new Address(original.ToString());
        }
        public override string ToString()
        {
            return (AddressValue ?? "")[..];
        }
        /// <summary>
        /// Checks if an address is <see langword="null"/> or it's internal <see cref="AddressValue"/> is <see langword="null"/>
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        public static bool IsNull(Address? address)
        {
            return address == null || address.AddressValue == null;
        }
        public override int GetHashCode()
        {
            return HashCode.Combine(AddressValue);
        }
        /// <summary>
        /// <see langword="true"/> if the address is considered empty by the tetronet standarts, otherwise - <see langword="false"/>
        /// </summary>
        public bool IsEmpty {
            get
            {
                return AddressValue == null || AddressValue.Trim() == "" || AddressValue.Trim() == "0";
            }
        }
    }
}
