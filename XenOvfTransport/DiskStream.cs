﻿/* Copyright (c) Citrix Systems, Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

/*
 * Based upon code copyrighted as below.
 */

//
// Copyright (c) 2008-2010, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.IO;
using DiscUtils.Streams;
using DiscUtils.Iscsi;


namespace XenOvfTransport
{
    // iSCSI disk stream to workaround a bug in DiscUtils.DiskStream.
    public class DiskStream : SparseStream
    {
        private Session _session;
        private long _lun;

        private long _length;
        private long _position;

        private int _blockSize;
        private bool _canWrite;
        private bool _canRead;

        public DiskStream(Session session, long lun, FileAccess access)
        {
            _session = session;
            _lun = lun;

            LunCapacity capacity = session.GetCapacity(lun);
            _blockSize = capacity.BlockSize;
////////////////////////////////////////////////////////////////////////////////////////////////////
            // CORRECTION
            // SCSI Read Capacity response includes the *last* logical block address (LBA) and NOT 
            // the logical block count as interpreted by DiscUtils.DiskStream.
            // Account for the last block.
            _length = (capacity.LogicalBlockCount + 1) * capacity.BlockSize;
////////////////////////////////////////////////////////////////////////////////////////////////////
            _canWrite = (access != FileAccess.Read);
            _canRead = (access != FileAccess.Write);
        }
        
        public override bool CanRead
        {
            get { return _canRead; }
        }

        public override bool CanSeek
        {
            get { return true; }
        }

        public override bool CanWrite
        {
            get { return _canWrite; }
        }

        public override void Flush()
        {
        }

        public override long Length
        {
            get { return _length; }
        }

        public override long Position
        {
            get
            {
                return _position;
            }
            set
            {
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!CanRead)
            {
                throw new InvalidOperationException("Attempt to read from read-only stream");
            }

            int maxToRead = (int)Math.Min(_length - _position, count);

            long firstBlock = _position / _blockSize;
////////////////////////////////////////////////////////////////////////////////////////////////////
            // CORRECTION
            // DiscUtils.Utilities is not accessible.
            //            long lastBlock = Utilities.Ceil(_position + maxToRead, _blockSize);
            long lastBlock = ((_position + maxToRead) + (_blockSize - 1)) / _blockSize;
////////////////////////////////////////////////////////////////////////////////////////////////////

            byte[] tempBuffer = new byte[(lastBlock - firstBlock) * _blockSize];
            int numRead = _session.Read(_lun, firstBlock, (short)(lastBlock - firstBlock), tempBuffer, 0);

            int numCopied = Math.Min(maxToRead, numRead);
            Array.Copy(tempBuffer, _position - (firstBlock * _blockSize), buffer, offset, numCopied);

            _position += numCopied;

            return numCopied;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long effectiveOffset = offset;
            if (origin == SeekOrigin.Current)
            {
                effectiveOffset += _position;
            }
            else if (origin == SeekOrigin.End)
            {
                effectiveOffset += _length;
            }

            if (effectiveOffset < 0)
            {
                throw new IOException("Attempt to move before beginning of disk");
            }
            else
            {
                _position = effectiveOffset;
                return _position;
            }
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!CanWrite)
            {
                throw new IOException("Attempt to write to read-only stream");
            }

            if (_position + count > _length)
            {
                throw new IOException("Attempt to write beyond end of stream");
            }

            int numWritten = 0;

            while (numWritten < count)
            {
                long block = _position / _blockSize;
                uint offsetInBlock = (uint)(_position % _blockSize);

                int toWrite = count - numWritten;

                // Need to read - we're not handling a full block
                if (offsetInBlock != 0 || toWrite < _blockSize)
                {
                    toWrite = (int)Math.Min(toWrite, _blockSize - offsetInBlock);

                    byte[] blockBuffer = new byte[_blockSize];
                    int numRead = _session.Read(_lun, block, 1, blockBuffer, 0);

                    if (numRead != _blockSize)
                    {
                        throw new IOException("Incomplete read, received " + numRead + " bytes from 1 block");
                    }

                    // Overlay as much data as we have for this block
                    Array.Copy(buffer, offset + numWritten, blockBuffer, offsetInBlock, toWrite);

                    // Write the block back
                    _session.Write(_lun, block, 1, _blockSize, blockBuffer, 0);
                }
                else
                {
                    // Processing at least one whole block, just write (after making sure to trim any partial sectors from the end)...
                    short numBlocks = (short)(toWrite / _blockSize);
                    toWrite = numBlocks * _blockSize;

                    _session.Write(_lun, block, numBlocks, _blockSize, buffer, offset + numWritten);
                }

                numWritten += toWrite;
                _position += toWrite;
            }
        }

        public override IEnumerable<StreamExtent> Extents
        {
            get
            {
                yield return new StreamExtent(0, _length);
            }
        }
    }
}
