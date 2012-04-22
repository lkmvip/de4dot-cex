﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using Mono.MyStuff;
using de4dot.PE;

namespace de4dot.code.deobfuscators.CliSecure {
	class CodeHeader {
		public byte[] signature;
		public byte[] decryptionKey;
		public uint totalCodeSize;
		public uint numMethods;
		public uint methodDefTableOffset;	// Relative to start of metadata
		public uint methodDefElemSize;
	}

	struct MethodInfo {
		public uint codeOffs, codeSize, flags, localVarSigTok;

		public MethodInfo(uint codeOffs, uint codeSize, uint flags, uint localVarSigTok) {
			this.codeOffs = codeOffs;
			this.codeSize = codeSize;
			this.flags = flags;
			this.localVarSigTok = localVarSigTok;
		}

		public override string ToString() {
			return string.Format("{0:X8} {1:X8} {2:X8} {3:X8}", codeOffs, codeSize, flags, localVarSigTok);
		}
	}

	class MethodsDecrypter {
		static byte[] normalSignature = new byte[16] { 0x08, 0x44, 0x65, 0xE1, 0x8C, 0x82, 0x13, 0x4C, 0x9C, 0x85, 0xB4, 0x17, 0xDA, 0x51, 0xAD, 0x25 };
		static byte[] proSignature    = new byte[16] { 0x68, 0xA0, 0xBB, 0x60, 0x13, 0x65, 0x5F, 0x41, 0xAE, 0x42, 0xAB, 0x42, 0x9B, 0x6B, 0x4E, 0xC1 };

		PeImage peImage;
		CodeHeader codeHeader = new CodeHeader();
		IDecrypter decrypter;

		interface IDecrypter {
			byte[] decrypt(MethodInfo methodInfo, out byte[] extraSections);
		}

		class DoNothingDecrypter : IDecrypter {
			PeImage peImage;

			public DoNothingDecrypter(PeImage peImage) {
				this.peImage = peImage;
			}

			public byte[] decrypt(MethodInfo methodInfo, out byte[] extraSections) {
				peImage.Reader.BaseStream.Position = methodInfo.codeOffs;
				byte[] code;
				MethodBodyParser.parseMethodBody(peImage.Reader, out code, out extraSections);
				return code;
			}
		}

		abstract class DecrypterBase : IDecrypter {
			protected PeImage peImage;
			protected CodeHeader codeHeader;
			protected uint endOfMetadata;

			public DecrypterBase(PeImage peImage, CodeHeader codeHeader) {
				this.peImage = peImage;
				this.codeHeader = codeHeader;
				endOfMetadata = peImage.rvaToOffset(peImage.Cor20Header.metadataDirectory.virtualAddress + peImage.Cor20Header.metadataDirectory.size);
			}

			public abstract byte[] decrypt(MethodInfo methodInfo, out byte[] extraSections);

			protected byte[] getCodeBytes(byte[] methodBody, out byte[] extraSections) {
				byte[] code;
				MethodBodyParser.parseMethodBody(new BinaryReader(new MemoryStream(methodBody)), out code, out extraSections);
				return code;
			}
		}

		class NormalDecrypter : DecrypterBase {
			public NormalDecrypter(PeImage peImage, CodeHeader codeHeader)
				: base(peImage, codeHeader) {
			}

			public override byte[] decrypt(MethodInfo methodInfo, out byte[] extraSections) {
				byte[] data = peImage.offsetReadBytes(endOfMetadata + methodInfo.codeOffs, (int)methodInfo.codeSize);
				for (int i = 0; i < data.Length; i++) {
					byte b = data[i];
					b ^= codeHeader.decryptionKey[(methodInfo.codeOffs - 0x30 + i) % 16];
					b ^= codeHeader.decryptionKey[(methodInfo.codeOffs - 0x30 + i + 7) % 16];
					data[i] = b;
				}
				return getCodeBytes(data, out extraSections);
			}
		}

		// Used when the anti-debugger protection is enabled
		class ProDecrypter : DecrypterBase {
			uint[] key = new uint[4];

			public ProDecrypter(PeImage peImage, CodeHeader codeHeader)
				: base(peImage, codeHeader) {
				for (int i = 0; i < 4; i++)
					key[i] = be_readUint32(codeHeader.decryptionKey, i * 4);
			}

			public override byte[] decrypt(MethodInfo methodInfo, out byte[] extraSections) {
				byte[] data = peImage.offsetReadBytes(endOfMetadata + methodInfo.codeOffs, (int)methodInfo.codeSize);

				int numQwords = (int)(methodInfo.codeSize / 8);
				for (int i = 0; i < numQwords; i++) {
					int offset = i * 8;
					uint q0 = be_readUint32(data, offset);
					uint q1 = be_readUint32(data, offset + 4);

					const uint magic = 0x9E3779B8;
					uint val = 0xC6EF3700;	// magic * 0x20
					for (int j = 0; j < 32; j++) {
						q1 -= ((q0 << 4) + key[2]) ^ (val + q0) ^ ((q0 >> 5) + key[3]);
						q0 -= ((q1 << 4) + key[0]) ^ (val + q1) ^ ((q1 >> 5) + key[1]);
						val -= magic;
					}

					be_writeUint32(data, offset, q0);
					be_writeUint32(data, offset + 4, q1);
				}

				return getCodeBytes(data, out extraSections);
			}

			static uint be_readUint32(byte[] data, int offset) {
				return (uint)((data[offset] << 24) +
						(data[offset + 1] << 16) +
						(data[offset + 2] << 8) +
						data[offset + 3]);
			}

			static void be_writeUint32(byte[] data, int offset, uint value) {
				data[offset] = (byte)(value >> 24);
				data[offset + 1] = (byte)(value >> 16);
				data[offset + 2] = (byte)(value >> 8);
				data[offset + 3] = (byte)value;
			}
		}

		interface ICsHeader {
			List<MethodInfo> getMethodInfos(uint codeHeaderOffset);
			void patchMethodDefTable(MetadataType methodDefTable, IList<MethodInfo> methodInfos);
			uint getMethodBodyOffset(MethodInfo methodInfo, uint methodDefElemOffset);
		}

		// CS 5.2+
		class CsHeader5 : ICsHeader {
			MethodsDecrypter methodsDecrypter;

			public CsHeader5(MethodsDecrypter methodsDecrypter) {
				this.methodsDecrypter = methodsDecrypter;
			}

			public List<MethodInfo> getMethodInfos(uint codeHeaderOffset) {
				uint offset = codeHeaderOffset + methodsDecrypter.codeHeader.totalCodeSize + 0x30;
				var methodInfos = new List<MethodInfo>((int)methodsDecrypter.codeHeader.numMethods);
				for (int i = 0; i < (int)methodsDecrypter.codeHeader.numMethods; i++, offset += 16) {
					uint codeOffs = methodsDecrypter.peImage.offsetReadUInt32(offset);
					uint codeSize = methodsDecrypter.peImage.offsetReadUInt32(offset + 4);
					uint flags = methodsDecrypter.peImage.offsetReadUInt32(offset + 8);
					uint localVarSigTok = methodsDecrypter.peImage.offsetReadUInt32(offset + 12);
					methodInfos.Add(new MethodInfo(codeOffs, codeSize, flags, localVarSigTok));
				}
				return methodInfos;
			}

			public void patchMethodDefTable(MetadataType methodDefTable, IList<MethodInfo> methodInfos) {
				uint offset = methodDefTable.fileOffset - methodDefTable.totalSize;
				foreach (var methodInfo in methodInfos) {
					offset += methodDefTable.totalSize;
					if (methodInfo.flags == 0 || methodInfo.codeOffs == 0)
						continue;
					uint rva = methodsDecrypter.peImage.offsetReadUInt32(offset);
					methodsDecrypter.peImage.writeUint16(rva, (ushort)methodInfo.flags);
					methodsDecrypter.peImage.writeUint32(rva + 8, methodInfo.localVarSigTok);
				}
			}

			public uint getMethodBodyOffset(MethodInfo methodInfo, uint methodDefElemOffset) {
				return methodsDecrypter.peImage.rvaToOffset(methodsDecrypter.peImage.offsetReadUInt32(methodDefElemOffset));
			}
		}

		// CS 4.0
		class CsHeader4 : ICsHeader {
			MethodsDecrypter methodsDecrypter;

			public CsHeader4(MethodsDecrypter methodsDecrypter) {
				this.methodsDecrypter = methodsDecrypter;
			}

			public List<MethodInfo> getMethodInfos(uint codeHeaderOffset) {
				uint offset = codeHeaderOffset + methodsDecrypter.codeHeader.totalCodeSize + 0x28;
				var methodInfos = new List<MethodInfo>((int)methodsDecrypter.codeHeader.numMethods);
				for (int i = 0; i < (int)methodsDecrypter.codeHeader.numMethods; i++, offset += 4) {
					uint codeOffs = methodsDecrypter.peImage.offsetReadUInt32(offset);
					if (codeOffs != 0)
						codeOffs += codeHeaderOffset;
					methodInfos.Add(new MethodInfo(codeOffs, 0, 0, 0));
				}
				return methodInfos;
			}

			public void patchMethodDefTable(MetadataType methodDefTable, IList<MethodInfo> methodInfos) {
			}

			public uint getMethodBodyOffset(MethodInfo methodInfo, uint methodDefElemOffset) {
				return methodInfo.codeOffs;
			}
		}

		public bool decrypt(PeImage peImage, string filename, CliSecureRtType csRtType, ref DumpedMethods dumpedMethods) {
			this.peImage = peImage;
			try {
				return decrypt2(ref dumpedMethods);
			}
			catch (InvalidMethodBody) {
				Log.w("Using dynamic method decryption");
				byte[] moduleCctorBytes = getModuleCctorBytes(csRtType);
				dumpedMethods = de4dot.code.deobfuscators.MethodsDecrypter.decrypt(filename, moduleCctorBytes);
				return true;
			}
		}

		static byte[] getModuleCctorBytes(CliSecureRtType csRtType) {
			var initMethod = csRtType.InitializeMethod;
			if (initMethod == null)
				return null;
			uint initToken = initMethod.MetadataToken.ToUInt32();
			var moduleCctorBytes = new byte[6];
			moduleCctorBytes[0] = 0x28;	// call
			moduleCctorBytes[1] = (byte)initToken;
			moduleCctorBytes[2] = (byte)(initToken >> 8);
			moduleCctorBytes[3] = (byte)(initToken >> 16);
			moduleCctorBytes[4] = (byte)(initToken >> 24);
			moduleCctorBytes[5] = 0x2A;	// ret
			return moduleCctorBytes;
		}

		bool isOldHeader(MetadataType methodDefTable) {
			if (methodDefTable.totalSize != codeHeader.methodDefElemSize)
				return true;
			if (methodDefTable.fileOffset - peImage.rvaToOffset(peImage.Cor20Header.metadataDirectory.virtualAddress) != codeHeader.methodDefTableOffset)
				return true;

			return false;
		}

		ICsHeader createCsHeader(MetadataType methodDefTable) {
			if (isOldHeader(methodDefTable)) {
				decrypter = new DoNothingDecrypter(peImage);
				return new CsHeader4(this);
			}
			return new CsHeader5(this);
		}

		static uint getCodeHeaderOffset(PeImage peImage) {
			return peImage.rvaToOffset(peImage.Cor20Header.metadataDirectory.virtualAddress + peImage.Cor20Header.metadataDirectory.size);
		}

		public bool decrypt2(ref DumpedMethods dumpedMethods) {
			uint codeHeaderOffset = getCodeHeaderOffset(peImage);
			if (!readCodeHeader(codeHeaderOffset))
				return false;

			var metadataTables = peImage.Cor20Header.createMetadataTables();
			var methodDefTable = metadataTables.getMetadataType(MetadataIndex.iMethodDef);

			var csHeader = createCsHeader(methodDefTable);
			var methodInfos = csHeader.getMethodInfos(codeHeaderOffset);
			csHeader.patchMethodDefTable(methodDefTable, methodInfos);

			dumpedMethods = new DumpedMethods();
			uint offset = methodDefTable.fileOffset;
			for (int i = 0; i < methodInfos.Count; i++, offset += methodDefTable.totalSize) {
				var methodInfo = methodInfos[i];
				if (methodInfo.codeOffs == 0)
					continue;

				var dm = new DumpedMethod();
				dm.token = 0x06000001 + (uint)i;

				uint methodBodyOffset = csHeader.getMethodBodyOffset(methodInfo, offset);
				dm.mdImplFlags = peImage.offsetReadUInt16(offset + (uint)methodDefTable.fields[1].offset);
				dm.mdFlags = peImage.offsetReadUInt16(offset + (uint)methodDefTable.fields[2].offset);
				dm.mdName = peImage.offsetRead(offset + (uint)methodDefTable.fields[3].offset, methodDefTable.fields[3].size);
				dm.mdSignature = peImage.offsetRead(offset + (uint)methodDefTable.fields[4].offset, methodDefTable.fields[4].size);
				dm.mdParamList = peImage.offsetRead(offset + (uint)methodDefTable.fields[5].offset, methodDefTable.fields[5].size);

				dm.code = decrypter.decrypt(methodInfo, out dm.extraSections);

				if ((peImage.offsetReadByte(methodBodyOffset) & 3) == 2) {
					dm.mhFlags = 2;
					dm.mhMaxStack = 8;
					dm.mhCodeSize = (uint)dm.code.Length;
					dm.mhLocalVarSigTok = 0;
				}
				else {
					dm.mhFlags = peImage.offsetReadUInt16(methodBodyOffset);
					dm.mhMaxStack = peImage.offsetReadUInt16(methodBodyOffset + 2);
					dm.mhCodeSize = (uint)dm.code.Length;
					dm.mhLocalVarSigTok = peImage.offsetReadUInt32(methodBodyOffset + 8);
				}

				dumpedMethods.add(dm);
			}

			return true;
		}

		bool readCodeHeader(uint offset) {
			codeHeader.signature = peImage.offsetReadBytes(offset, 16);
			codeHeader.decryptionKey = peImage.offsetReadBytes(offset + 0x10, 16);
			codeHeader.totalCodeSize = peImage.offsetReadUInt32(offset + 0x20);
			codeHeader.numMethods = peImage.offsetReadUInt32(offset + 0x24);
			codeHeader.methodDefTableOffset = peImage.offsetReadUInt32(offset + 0x28);
			codeHeader.methodDefElemSize = peImage.offsetReadUInt32(offset + 0x2C);

			if (Utils.compare(codeHeader.signature, normalSignature))
				decrypter = new NormalDecrypter(peImage, codeHeader);
			else if (Utils.compare(codeHeader.signature, proSignature))
				decrypter = new ProDecrypter(peImage, codeHeader);
			else
				return false;

			if (codeHeader.totalCodeSize > 0x10000000)
				return false;
			if (codeHeader.numMethods > 512*1024)
				return false;

			return true;
		}

		public static bool detect(PeImage peImage) {
			try {
				uint codeHeaderOffset = getCodeHeaderOffset(peImage);
				var sig = peImage.offsetReadBytes(codeHeaderOffset, 16);
				return Utils.compare(sig, normalSignature) || Utils.compare(sig, proSignature);
			}
			catch {
				return false;
			}
		}
	}
}
