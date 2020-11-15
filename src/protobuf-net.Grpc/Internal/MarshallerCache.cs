﻿using Grpc.Core;
using ProtoBuf.Grpc.Configuration;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ProtoBuf.Grpc.Internal
{
    internal sealed class MarshallerCache
    {
        private readonly MarshallerFactory[] _factories;

        public MarshallerCache(MarshallerFactory[] factories, MarshallerCache? innerCache = null)
        {
            _factories = factories ?? throw new ArgumentNullException(nameof(factories));
            if (innerCache != null) _marshallers = innerCache._marshallers;
        }

        internal bool CanSerializeType(Type type)
        {
            if (_marshallers.TryGetValue(type, out var obj)) return obj != null;
            return SlowImpl(this, type);

            static bool SlowImpl(MarshallerCache obj, Type type)
                => _createAndAdd.MakeGenericMethod(type).Invoke(obj, Array.Empty<object>()) != null;
        }
        static readonly MethodInfo _createAndAdd = typeof(MarshallerCache).GetMethod(
            nameof(CreateAndAdd), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly ConcurrentDictionary<Type, object?> _marshallers
            = new ConcurrentDictionary<Type, object?>
        {
#pragma warning disable CS0618 // Empty
            [typeof(Empty)] = Empty.Marshaller
#pragma warning restore CS0618
        };

        internal Marshaller<T> GetMarshaller<T>()
        {
            return (_marshallers.TryGetValue(typeof(T), out var obj)
                ? (Marshaller<T>?)obj : CreateAndAdd<T>()) ?? Throw();

            static Marshaller<T> Throw() => throw new InvalidOperationException("No marshaller available for " + typeof(T).FullName);
        }

        internal void SetMarshaller<T>(Marshaller<T>? marshaller)
        {
            if (marshaller is null)
            {
                _marshallers.TryRemove(typeof(T), out _);
            }
            else
            {
                _marshallers[typeof(T)] = marshaller;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Marshaller<T>? CreateAndAdd<T>()
        {
            object? obj = CreateMarshaller<T>();
            if (!_marshallers.TryAdd(typeof(T), obj)) obj = _marshallers[typeof(T)];
            return obj as Marshaller<T>;
        }
        private Marshaller<T>? CreateMarshaller<T>()
        {
            foreach (var factory in _factories)
            {
                if (factory.CanSerialize(typeof(T)))
                    return factory.CreateMarshaller<T>(); 
            }
            return null;
        }

        internal MarshallerFactory? TryGetFactory(Type type)
        {
            foreach (var factory in _factories)
            {
                if (factory.CanSerialize(type))
                    return factory;
            }
            return null;
        }

        internal TFactory? TryGetFactory<TFactory>()
            where TFactory : MarshallerFactory
        {
            foreach (var factory in _factories)
            {
                if (factory is TFactory typed)
                    return typed;
            }
            return null;
        }
    }
}
