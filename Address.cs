using System.Numerics;

namespace ModemAPI
{
    public class Address : IEquatable<Address>
    {
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
            return AddressValue ?? "";
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(AddressValue);
        }
        public bool IsEmpty {
            get
            {
                return AddressValue == null || AddressValue.Trim() == "" || AddressValue.Trim() == "0";
            }
        }

        /*public static bool operator ==(Address? x, Address? y)
        {
            return (x ?? new()).Equals(y);
        }
        public static bool operator !=(Address? x, Address? y)
        {
            return !(x ?? new()).Equals(y);
        }*/
    }
}
