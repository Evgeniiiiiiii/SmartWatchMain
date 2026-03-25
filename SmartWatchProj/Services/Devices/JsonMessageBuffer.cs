using System.Text;

namespace SmartWatchProj.Services.Devices
{
    internal sealed class JsonMessageBuffer
    {
        private readonly StringBuilder _buffer = new();

        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            _buffer.Append(chunk);
        }

        public bool TryReadNext(out string? json)
        {
            json = null;

            if (_buffer.Length == 0)
            {
                return false;
            }

            var startIndex = FindJsonStart();
            if (startIndex < 0)
            {
                if (_buffer.Length > 8192)
                {
                    _buffer.Clear();
                }

                return false;
            }

            if (startIndex > 0)
            {
                _buffer.Remove(0, startIndex);
            }

            var depth = 0;
            var inString = false;
            var isEscaped = false;

            for (var i = 0; i < _buffer.Length; i++)
            {
                var current = _buffer[i];

                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        isEscaped = true;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current != '}')
                {
                    continue;
                }

                depth--;

                if (depth == 0)
                {
                    json = _buffer.ToString(0, i + 1);
                    _buffer.Remove(0, i + 1);
                    TrimNonJsonPrefix();
                    return true;
                }

                if (depth < 0)
                {
                    _buffer.Clear();
                    return false;
                }
            }

            return false;
        }

        public void Reset()
        {
            _buffer.Clear();
        }

        private int FindJsonStart()
        {
            for (var i = 0; i < _buffer.Length; i++)
            {
                if (_buffer[i] == '{')
                {
                    return i;
                }
            }

            return -1;
        }

        private void TrimNonJsonPrefix()
        {
            var prefixLength = 0;

            while (prefixLength < _buffer.Length && char.IsWhiteSpace(_buffer[prefixLength]))
            {
                prefixLength++;
            }

            while (prefixLength < _buffer.Length && _buffer[prefixLength] != '{')
            {
                prefixLength++;
            }

            if (prefixLength > 0)
            {
                _buffer.Remove(0, prefixLength);
            }
        }
    }
}
