using System;
using System.Linq;
using System.Reflection;
using Grpc.Core;
using ProtoBuf.Grpc.Configuration;

namespace ProtoBuf.Grpc.Internal
{
    public class ValueTypeWrapper<T> where T : struct
    {
        public T Value;

        public ValueTypeWrapper(T value)
        {
            Value = value;
        }
    }

    internal class ValueTypeWrapperMarshallerFactory : MarshallerFactory
    {
        private MarshallerCache _cache;
        static readonly MethodInfo s_invokeDeserializer = typeof(ValueTypeWrapperMarshallerFactory).GetMethod(
            nameof(InvokeDeserializer), BindingFlags.Instance | BindingFlags.NonPublic)!;
        static readonly MethodInfo s_invokeSerializer = typeof(ValueTypeWrapperMarshallerFactory).GetMethod(
            nameof(InvokeSerializer), BindingFlags.Instance | BindingFlags.NonPublic)!;

        internal ValueTypeWrapperMarshallerFactory(MarshallerCache cache)
        {
            _cache = cache;
        }

        protected internal override bool CanSerialize(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTypeWrapper<>))
                type = type.GetGenericArguments()[0];

            return _cache.TryGetFactory<MarshallerFactory>()!.CanSerialize(type);
        }

        protected internal override T Deserialize<T>(byte[] payload)
        {
            var type = typeof(T);
            var factory = _cache.TryGetFactory<MarshallerFactory>()!;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTypeWrapper<>))
            {
                type = type.GetGenericArguments()[0];
                return (T) s_invokeDeserializer.MakeGenericMethod(type).Invoke(this, new object?[] {factory, payload});
            }

            return factory.Deserialize<T>(payload);
        }

        protected internal override byte[] Serialize<T>(T value)
        {
            var type = typeof(T);
            var factory = _cache.TryGetFactory<MarshallerFactory>()!;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTypeWrapper<>))
            {
                type = type.GetGenericArguments()[0];
                return (byte[]) s_invokeSerializer.MakeGenericMethod(type).Invoke(this, new object?[] {factory, value});
            }

            return factory.Serialize(value);
        }
    
        private ValueTypeWrapper<T> InvokeDeserializer<T>(MarshallerFactory factory, byte[] payload) where T : struct
        {
            return new ValueTypeWrapper<T>(factory.Deserialize<T>(payload));
        }

        private byte[] InvokeSerializer<T>(MarshallerFactory factory, ValueTypeWrapper<T> value) where T : struct
        {
            return factory.Serialize(value.Value);
        }
    }
}
