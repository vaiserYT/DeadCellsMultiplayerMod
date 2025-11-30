using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using dc.haxe.io;
using dc.hxbit;

namespace DeadCellsMultiplayerMod
{
    internal static class HaxeProxySerializer
    {
        private sealed class Packet
        {
            public string payload = string.Empty;
            public string hx = string.Empty;
            public object? extra;
            public bool hxbit;
        }

        private static readonly Type? SerializeContextType = Type.GetType("ModCore.Serialization.SerializeContext, ModCore");
        private static readonly Type? DeserializeContextType = Type.GetType("ModCore.Serialization.DeserializeContext, ModCore");

        public static string Serialize(object value)
        {
            if (value == null) return string.Empty;

            try
            {
                // Prefer hxbit binary path for Hashlink objects
                return SerializeHxbit(value);
            }
            catch
            {
                // fallback: payload-only packet to avoid hard failure
                var packet = new Packet
                {
                    payload = JsonConvert.SerializeObject(value),
                    hx = string.Empty,
                    extra = null,
                    hxbit = false
                };
                return JsonConvert.SerializeObject(packet);
            }
        }

        public static T Deserialize<T>(string payload) => (T)Deserialize(typeof(T), payload)!;

        public static object? Deserialize(Type targetType, string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;

            var packet = JsonConvert.DeserializeObject<Packet>(payload);
            if (packet == null) return null;

            // hxbit binary path first
            if (!string.IsNullOrEmpty(packet.hx))
            {
                var hxBytes = System.Convert.FromBase64String(packet.hx);
                var obj = DeserializeHxbit(hxBytes);
                if (obj != null)
                {
                    return obj;
                }
            }

            // fallback: payload path
            var serializer = new Serializer();
            var ctx = CreateContext(DeserializeContextType, serializer);
            PushContext(DeserializeContextType, ctx);

            IntPtr bufferPtr = IntPtr.Zero;
            try
            {
                var hxBytes = string.IsNullOrEmpty(packet.hx) ? Array.Empty<byte>() : System.Convert.FromBase64String(packet.hx);
                bufferPtr = Marshal.AllocHGlobal(hxBytes.Length > 0 ? hxBytes.Length : 1);
                if (hxBytes.Length > 0)
                    Marshal.Copy(hxBytes, 0, bufferPtr, hxBytes.Length);

                var bytes = new Bytes(bufferPtr, hxBytes.Length);
                var posRef = CreateIntRef();
                serializer.GetType().GetMethod("beginLoad")?.Invoke(serializer, new[] { bytes, posRef });
                if (packet.extra != null && ctx != null)
                {
                    InvokeInstance(ctx, "Begin", packet.extra);
                }

                var result = JsonConvert.DeserializeObject(packet.payload ?? string.Empty, targetType);
                serializer.endLoad();
                return result;
            }
            finally
            {
                PopContext(DeserializeContextType);
                if (bufferPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(bufferPtr);
            }
        }

        private static byte[] CopyBuffer(BytesBuffer buffer)
        {
            if (buffer.pos <= 0) return Array.Empty<byte>();

            var managed = new byte[buffer.pos];
            Marshal.Copy(buffer.b, managed, 0, buffer.pos);
            return managed;
        }

        private static object? CreateContext(Type? ctxType, Serializer serializer)
        {
            if (ctxType == null) return null;
            return Activator.CreateInstance(ctxType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { serializer }, null);
        }

        private static void PushContext(Type? ctxType, object? ctx)
        {
            if (ctxType == null || ctx == null) return;
            ctxType.GetMethod("PushContext", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { ctx });
        }

        private static void PopContext(Type? ctxType)
        {
            if (ctxType == null) return;
            ctxType.GetMethod("PopContext", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }

        private static BytesBuffer? GetHxbitBuffer(object? ctx)
        {
            if (ctx == null) return null;
            var prop = ctx.GetType().GetProperty("hxbitBuffer", BindingFlags.Instance | BindingFlags.Public);
            return (BytesBuffer?)prop?.GetValue(ctx);
        }

        private static object? InvokeInstance(object? ctx, string methodName, params object?[] args)
        {
            if (ctx == null) return null;
            var mi = ctx.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            return mi?.Invoke(ctx, args);
        }

        private static object? CreateIntRef()
        {
            var refType = Type.GetType("dc.hl.types.Ref`1[[System.Int32, System.Private.CoreLib]], GameProxy");
            return refType != null
                ? Activator.CreateInstance(refType, BindingFlags.Public | BindingFlags.Instance, null, null, null)
                : null;
        }

        private static string SerializeHxbit(object value)
        {
            var serializer = new Serializer();
            var posRef = CreateIntRef();

            serializer.beginSave();
            // addAnyRef will register hxbit object and assign uid
            serializer.GetType().GetMethod("addAnyRef")?.Invoke(serializer, new[] { value });
            var endSave = serializer.GetType().GetMethod("endSave");
            var bytesObj = endSave?.Invoke(serializer, new[] { posRef }) as Bytes;
            var bytes = bytesObj != null ? BytesToArray(bytesObj) : Array.Empty<byte>();

            var packet = new Packet
            {
                payload = string.Empty,
                hx = System.Convert.ToBase64String(bytes),
                extra = null,
                hxbit = true
            };
            return JsonConvert.SerializeObject(packet);
        }

        private static object? DeserializeHxbit(byte[] hxBytes)
        {
            if (hxBytes.Length == 0) return null;

            var serializer = new Serializer();
            var posRef = CreateIntRef();

            IntPtr bufferPtr = Marshal.AllocHGlobal(hxBytes.Length);
            try
            {
                Marshal.Copy(hxBytes, 0, bufferPtr, hxBytes.Length);
                var bytes = new Bytes(bufferPtr, hxBytes.Length);
                serializer.GetType().GetMethod("beginLoad")?.Invoke(serializer, new object?[] { bytes, posRef });
                var obj = serializer.GetType().GetMethod("getAnyRef")?.Invoke(serializer, null);
                serializer.endLoad();
                return obj;
            }
            finally
            {
                Marshal.FreeHGlobal(bufferPtr);
            }
        }

        private static byte[] BytesToArray(Bytes bytes)
        {
            if (bytes.length <= 0) return Array.Empty<byte>();
            var arr = new byte[bytes.length];
            Marshal.Copy(bytes.b, arr, 0, bytes.length);
            return arr;
        }
    }
}
