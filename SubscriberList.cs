namespace ModemAPI
{
    /// <summary>
    /// Thread-safe list of receive event subscribers. Adding and removing copies the array, so dispatching never takes a lock
    /// and a subscriber may attach or detach other subscribers while an event is being dispatched.
    /// </summary>
    internal sealed class SubscriberList<T>
    {
        private Action<T, Action>[] Subscribers = [];
        private readonly Lock SubscribersLock = new();

        public int Count { get { return Volatile.Read(ref Subscribers).Length; } }

        public void Add(Action<T, Action> subscriber)
        {
            lock (SubscribersLock)
            {
                Subscribers = [.. Subscribers, subscriber];
            }
        }

        public bool Remove(Action<T, Action> subscriber)
        {
            lock (SubscribersLock)
            {
                int index = Array.IndexOf(Subscribers, subscriber);
                if (index < 0)
                {
                    return false;
                }
                Action<T, Action>[] result = new Action<T, Action>[Subscribers.Length - 1];
                Array.Copy(Subscribers, 0, result, 0, index);
                Array.Copy(Subscribers, index + 1, result, index, Subscribers.Length - index - 1);
                Subscribers = result;
                return true;
            }
        }

        public void Invoke(T value, Action stopLoop)
        {
            foreach (Action<T, Action> subscriber in Volatile.Read(ref Subscribers))
            {
                subscriber(value, stopLoop);
            }
        }
    }
}
