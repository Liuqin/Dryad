/*
Copyright (c) Microsoft Corporation

All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in 
compliance with the License.  You may obtain a copy of the License 
at http://www.apache.org/licenses/LICENSE-2.0   


THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, EITHER 
EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF 
TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.  


See the Apache Version 2.0 License for specific language governing permissions and 
limitations under the License. 

*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Research.DryadLinq;

namespace Microsoft.Research.DryadLinq.Internal
{
    /// <summary>
    /// The DryadLINQ class to write texts to a native stream. 
    /// </summary>
    /// <remarks>A DryadLINQ user should not need to use this class directly.</remarks>
    public unsafe sealed class DryadLinqTextWriter
    {
        private const int DefaultBlockSize = 256 * 1024;
        private const string NewLine = "\r\n";
        
        private NativeBlockStream m_nativeStream;
        private Encoding m_encoding;
        private Int32 m_nextBlockSize;
        private Int32 m_bufferSizeHint;
        private DataBlockInfo m_curDataBlockInfo;
        private byte* m_curDataBlock;
        private Int32 m_curBlockSize;
        private Int32 m_curLineStart;
        private Int32 m_curLineEnd;
        private Int64 m_numBytesWritten;
        private bool m_calcFP;
        private bool m_isClosed;
        private bool m_isASCIIOrUTF8;

        /// <summary>
        /// Initializes an instance of DryadLinqTextWriter with encoding UTF8.
        /// </summary>
        /// <param name="stream">A native stream to write to.</param>
        public DryadLinqTextWriter(NativeBlockStream stream)
            : this(stream, Encoding.UTF8)
        {
        }

        /// <summary>
        /// Initializes an instance of DryadLinqTextWriter.
        /// </summary>
        /// <param name="stream">A native stream to write to.</param>
        /// <param name="encoding">The text encoding.</param>
        public DryadLinqTextWriter(NativeBlockStream stream, Encoding encoding)
            : this(stream, encoding, DefaultBlockSize)
        {
        }

        /// <summary>
        /// Initializes an instance of DryadLinqTextWriter.
        /// </summary>
        /// <param name="stream">A native stream to write to.</param>
        /// <param name="encoding">The text encoding.</param>
        /// <param name="buffSize">A hint for the size of write buffer.</param>
        public DryadLinqTextWriter(NativeBlockStream stream, Encoding encoding, Int32 buffSize)
        {
            this.m_nativeStream = stream;
            this.m_encoding = encoding;
            this.m_nextBlockSize = Math.Max(DefaultBlockSize, buffSize/2);
            this.m_bufferSizeHint = buffSize;
            this.m_curDataBlockInfo.DataBlock = null;
            this.m_curDataBlockInfo.BlockSize = 0;
            this.m_curDataBlockInfo.ItemHandle = IntPtr.Zero;            
            this.m_curDataBlock = this.m_curDataBlockInfo.DataBlock;
            this.m_curBlockSize = this.m_curDataBlockInfo.BlockSize;
            this.m_curLineStart = 0;
            this.m_curLineEnd = 0;
            this.m_numBytesWritten = 0;
            this.m_calcFP = false;
            this.m_isClosed = false;
            this.m_isASCIIOrUTF8 = (encoding == Encoding.UTF8 || encoding == Encoding.ASCII);
        }

        /// <summary>
        /// Initializes an instance of DryadLiqnTextWriter with encoding UTF8.
        /// </summary>
        /// <param name="vertexInfo">A native handle for Dryad vertex.</param>
        /// <param name="portNum">A port number that specifies a Dryad channel.</param>
        /// <param name="buffSize">A hint for the size of write buffer.</param>
        public DryadLinqTextWriter(IntPtr vertexInfo, UInt32 portNum, Int32 buffSize)
            : this(new DryadLinqChannel(vertexInfo, portNum, false), Encoding.UTF8, buffSize)
        {
        }

        /// <summary>
        /// Initializes an instance of DryadLiqnTextWriter with encoding UTF8.
        /// </summary>
        /// <param name="vertexInfo">A native handle for Dryad vertex.</param>
        /// <param name="portNum">A port number that specifies a Dryad channel.</param>
        /// <param name="encoding">The text encoding.</param>
        /// <param name="buffSize">A hint for the size of write buffer.</param>
        public DryadLinqTextWriter(IntPtr vertexInfo, UInt32 portNum, Encoding encoding, Int32 buffSize)
            : this(new DryadLinqChannel(vertexInfo, portNum, false), encoding, buffSize)
        {
        }

        /// <summary>
        /// The finalizer that frees native resources.
        /// </summary>
        ~DryadLinqTextWriter()
        {
            // Only release native resoure here
            if (this.m_curDataBlockInfo.ItemHandle != IntPtr.Zero)
            {
                this.m_nativeStream.ReleaseDataBlock(this.m_curDataBlockInfo.ItemHandle);
                this.m_curDataBlockInfo.ItemHandle = IntPtr.Zero;
            }
        }

        /// <summary>
        /// A hint for the size of write buffer.
        /// </summary>
        public Int32 BufferSizeHint
        {
            get { return this.m_bufferSizeHint; }
        }

        internal string GetChannelURI()
        {
            return this.m_nativeStream.GetURI();
        }

        internal Int64 GetTotalLength()
        {
            return this.m_nativeStream.GetTotalLength();
        }

        internal UInt64 GetFingerPrint()
        {
            if (!this.m_calcFP)
            {
                throw new DryadLinqException(DryadLinqErrorCode.FingerprintDisabled, SR.FingerprintDisabled);
            }
            return this.m_nativeStream.GetFingerPrint();
        }

        /// <summary>
        /// Gets and sets the fingerprint of the content of the writer.
        /// </summary>
        public bool CalcFP
        {
            get { return this.m_calcFP; }
            set { this.m_calcFP = value; }
        }

        /// <summary>
        /// Writes a specified line of text to the writer.
        /// </summary>
        /// <param name="line">The line to write.</param>
        /// <returns>The number of bytes used to represent the line.</returns>
        public unsafe int WriteLine(string line)
        {
            Int32 strLen = line.Length;
            Int32 maxByteCount = this.m_encoding.GetMaxByteCount(strLen + 2);

            while (this.m_curBlockSize - this.m_curLineEnd < maxByteCount)
            {
                this.FlushDataBlock();
            }
            
            Int32 numBytes;
            fixed (char* pLine = line)
            {
                numBytes = this.m_encoding.GetBytes(pLine,
                                                    strLen,
                                                    this.m_curDataBlock + this.m_curLineEnd,
                                                    this.m_curBlockSize - this.m_curLineEnd);
            }
            this.m_curLineEnd += numBytes;

            Int32 numBytes1 = 2;
            if (this.m_isASCIIOrUTF8)
            {
                this.m_curDataBlock[this.m_curLineEnd] = 0x0d;
                this.m_curDataBlock[this.m_curLineEnd+1] = 0x0a;
                this.m_curLineEnd += numBytes1;
            }
            else
            {
                fixed (char* pNewLine = NewLine)
                {
                    numBytes1 = this.m_encoding.GetBytes(pNewLine, 
                                                         NewLine.Length, 
                                                         this.m_curDataBlock + this.m_curLineEnd,
                                                         this.m_curBlockSize - this.m_curLineEnd);
                }
                this.m_curLineEnd += numBytes1;
            }
            this.m_curLineStart = this.m_curLineEnd;
            return numBytes + numBytes1;
        }

        /// <summary>
        /// Flushes the current write buffer.
        /// </summary>
        public void Flush()
        {
            Debug.Assert(this.m_curLineStart == this.m_curLineEnd);
            if (this.m_curLineStart > 0)
            {
                this.m_nativeStream.WriteDataBlock(this.m_curDataBlockInfo.ItemHandle, this.m_curLineStart);
                this.m_numBytesWritten += this.m_curLineStart;
                this.m_nativeStream.ReleaseDataBlock(this.m_curDataBlockInfo.ItemHandle);
                this.m_curDataBlockInfo.ItemHandle = IntPtr.Zero;
                this.m_curDataBlockInfo = this.m_nativeStream.AllocateDataBlock(this.m_curBlockSize);
                this.m_curDataBlock = this.m_curDataBlockInfo.DataBlock;
                this.m_curBlockSize = this.m_curDataBlockInfo.BlockSize;
                this.m_curLineStart = 0;
                this.m_curLineEnd = 0;
            }
            
            this.m_nativeStream.Flush();
        }
        
        /// <summary>
        /// Flushes the write buffer and closes the writer.
        /// </summary>
        public void Close()
        {
            if (!this.m_isClosed)
            {
                this.m_isClosed = true;
                this.Flush();
                this.m_nativeStream.ReleaseDataBlock(this.m_curDataBlockInfo.ItemHandle);
                this.m_curDataBlockInfo.ItemHandle = IntPtr.Zero;
                this.m_nativeStream.Close();
            }
            GC.SuppressFinalize(this);
        }

        private void FlushDataBlock()
        {
            DataBlockInfo newDataBlockInfo;
            if (this.m_curLineStart == 0)
            {
                // The current block is too small for a single record, augment it
                if (this.m_curBlockSize == this.m_nextBlockSize)
                {
                    throw new DryadLinqException(DryadLinqErrorCode.RecordSizeMax2GB, SR.RecordSizeMax2GB);
                }
                newDataBlockInfo = this.m_nativeStream.AllocateDataBlock(this.m_nextBlockSize);
                this.m_nextBlockSize = this.m_nextBlockSize * 2;
                if (this.m_nextBlockSize < 0)
                {
                    this.m_nextBlockSize = 0x7FFFFFF8;
                }
                this.m_nativeStream.ReleaseDataBlock(this.m_curDataBlockInfo.ItemHandle);
                this.m_curDataBlockInfo.ItemHandle = IntPtr.Zero;
            }
            else
            {
                // Write all the complete records in the block
                this.m_nativeStream.WriteDataBlock(this.m_curDataBlockInfo.ItemHandle, this.m_curLineStart);
                this.m_numBytesWritten += this.m_curLineStart;
                this.m_nativeStream.ReleaseDataBlock(this.m_curDataBlockInfo.ItemHandle);
                this.m_curDataBlockInfo.ItemHandle = IntPtr.Zero;
                newDataBlockInfo = this.m_nativeStream.AllocateDataBlock(this.m_curBlockSize);                
                this.m_curLineEnd -= this.m_curLineStart;                
                this.m_curLineStart = 0;
            }
            this.m_curDataBlockInfo = newDataBlockInfo;
            this.m_curDataBlock = newDataBlockInfo.DataBlock;
            this.m_curBlockSize = newDataBlockInfo.BlockSize;
        }

        /// <summary>
        /// The size in bytes of the current content of the writer.
        /// </summary>
        public Int64 Length 
        {
            get {
                return this.m_numBytesWritten + this.m_curLineEnd;
            }
        }

        /// <summary>
        /// Returns a string that represents this DryadLinqTextWriter object.
        /// </summary>
        /// <returns>The string representation.</returns>
        public override string ToString()
        {
            return this.m_nativeStream.ToString();
        }
    }

}
