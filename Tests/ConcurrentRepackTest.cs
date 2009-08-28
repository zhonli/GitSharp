/*
 * Copyright (C) 2009, Robin Rosenberg <robin.rosenberg@dewire.com>
 * Copyright (C) 2009, Google Inc.
 *
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or
 * without modification, are permitted provided that the following
 * conditions are met:
 *
 * - Redistributions of source code must retain the above copyright
 *   notice, this list of conditions and the following disclaimer.
 *
 * - Redistributions in binary form must reproduce the above
 *   copyright notice, this list of conditions and the following
 *   disclaimer in the documentation and/or other materials provided
 *   with the distribution.
 *
 * - Neither the name of the Git Development Community nor the
 *   names of its contributors may be used to endorse or promote
 *   products derived from this software without specific prior
 *   written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
 * OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
 * NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
 * ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Threading;
using GitSharp.Exceptions;
using GitSharp.RevWalk;
using NUnit.Framework;

namespace GitSharp.Tests
{
	[TestFixture]
	public class ConcurrentRepackTest : RepositoryTestCase
	{
		public override void setUp()
		{
			var windowCacheConfig = new WindowCacheConfig();
			windowCacheConfig.setPackedGitOpenFiles(1);
			WindowCache.reconfigure(windowCacheConfig);
			base.setUp();
		}

		public override void tearDown()
		{
			base.tearDown();
			var windowCacheConfig = new WindowCacheConfig();
			WindowCache.reconfigure(windowCacheConfig);
		}

		[Test]
		public void testObjectInNewPack()
		{
			// Create a new object in a new pack, and test that it is present.
			//
			Repository eden = createNewEmptyRepo();
			RevObject o1 = writeBlob(eden, "o1");
			pack(eden, o1);
			Assert.AreEqual(o1.Name, parse(o1).Name);
		}

		[Test]
		public void testObjectMovedToNewPack1()
		{
			// Create an object and pack it. Then remove that pack and put the
			// object into a different pack file, with some other object. We
			// still should be able to access the objects.
			//
			Repository eden = createNewEmptyRepo();
			RevObject o1 = writeBlob(eden, "o1");
			FileInfo[] out1 = pack(eden, o1);
			Assert.AreEqual(o1.Name, parse(o1).Name);

			RevObject o2 = writeBlob(eden, "o2");
			pack(eden, o2, o1);

			// Force close, and then delete, the old pack.
			//
			whackCache();
			delete(out1);

			// Now here is the interesting thing. Will git figure the new
			// object exists in the new pack, and not the old one.
			//
			Assert.AreEqual(o2.Name, parse(o2).Name);
			Assert.AreEqual(o1.Name, parse(o1).Name);
		}

		[Test]
		public void testObjectMovedWithinPack()
		{
			// Create an object and pack it.
			//
			Repository eden = createNewEmptyRepo();
			RevObject o1 = writeBlob(eden, "o1");
			FileInfo[] out1 = pack(eden, o1);
			Assert.AreEqual(o1.Name, parse(o1).Name);

			// Force close the old pack.
			//
			whackCache();

			// Now overwrite the old pack in place. This method of creating a
			// different pack under the same file name is partially broken. We
			// should also have a different file name because the list of objects
			// within the pack has been modified.
			//
			RevObject o2 = writeBlob(eden, "o2");
			var pw = new PackWriter(eden, NullProgressMonitor.Instance);
			pw.addObject(o2);
			pw.addObject(o1);
			write(out1, pw);

			// Try the old name, then the new name. The old name should cause the
			// pack to reload when it opens and the index and pack mismatch.
			//
			Assert.AreEqual(o1.Name, parse(o1).Name);
			Assert.AreEqual(o2.Name, parse(o2).Name);
		}

		[Test]
		public void testObjectMovedToNewPack2()
		{
			// Create an object and pack it. Then remove that pack and put the
			// object into a different pack file, with some other object. We
			// still should be able to access the objects.
			//
			Repository eden = createNewEmptyRepo();
			RevObject o1 = writeBlob(eden, "o1");
			FileInfo[] out1 = pack(eden, o1);
			Assert.AreEqual(o1.Name, parse(o1).Name);

			ObjectLoader load1 = db.OpenBlob(o1);
			Assert.IsNotNull(load1);

			RevObject o2 = writeBlob(eden, "o2");
			pack(eden, o2, o1);

			// Force close, and then delete, the old pack.
			//
			whackCache();
			delete(out1);

			// Now here is the interesting thing... can the loader we made
			// earlier still resolve the object, even though its underlying
			// pack is gone, but the object still exists.
			//
			ObjectLoader load2 = db.OpenBlob(o1);
			Assert.IsNotNull(load2);
			Assert.AreNotSame(load1, load2);

			byte[] data2 = load2.getCachedBytes();
			byte[] data1 = load1.getCachedBytes();
			Assert.IsNotNull(data2);
			Assert.IsNotNull(data1);
			Assert.AreNotSame(data1, data2); // cache should be per-pack, not per object
			Assert.IsTrue(Equals(data1, data2));
			Assert.AreEqual(load2.getType(), load1.getType());
		}

		private static void whackCache()
		{
			var config = new WindowCacheConfig();
			config.setPackedGitOpenFiles(1);
			WindowCache.reconfigure(config);
		}

		private RevObject parse(AnyObjectId id)
		{
			return new GitSharp.RevWalk.RevWalk(db).parseAny(id);
		}

		private FileInfo[] pack(Repository src, params RevObject[] list)
		{
			var pw = new PackWriter(src, NullProgressMonitor.Instance);
			foreach (RevObject o in list)
			{
				pw.addObject(o);
			}

			ObjectId name = pw.computeName();
			FileInfo packFile = fullPackFileName(name, ".pack");
			FileInfo idxFile = fullPackFileName(name, ".idx");
			var files = new[] { packFile, idxFile };
			write(files, pw);
			return files;
		}

		private static void write(FileInfo[] files, PackWriter pw)
		{
			FileInfo file = files[0];
			long begin = file.Directory.LastWriteTime.Ticks;
			
			using(var stream = file.Create())
			{
				try
				{
					pw.writePack(stream);
				}
				catch (Exception)
				{
					stream.Close();
				}
			}

			file = files[1];
			using (var stream = file.Create())
			{
				try
				{
					pw.writeIndex(stream);
				}
				catch (Exception)
				{
					stream.Close();
				}
			}

			touch(begin, files[0].Directory);
		}

		private static void delete(FileInfo[] list)
		{
			long begin = list[0].Directory.LastWriteTime.Ticks;
			foreach (var fi in list)
			{
				try
				{
					fi.Delete();
				}
				catch (IOException)
				{
				}
				Assert.IsFalse(File.Exists(fi.FullName), fi + " was not removed");
			}

			touch(begin, list[0].Directory);
		}

		private static void touch(long begin, FileSystemInfo dir)
		{
			while (begin >= dir.LastAccessTime.Ticks)
			{
				try
				{
					Thread.Sleep(25);
				}
				catch (IOException)
				{
					//
				}
				dir.LastAccessTime = DateTime.Now;
			}
		}

		private FileInfo fullPackFileName(AnyObjectId name, string suffix)
		{
			var packdir = Path.Combine(db.getObjectDatabase().getDirectory().FullName, "pack");
			return new FileInfo(Path.Combine(packdir, "pack-" + name.Name + suffix));
		}

		private RevObject writeBlob(Repository repo, string data)
		{
			var revWalk = new GitSharp.RevWalk.RevWalk(repo);
			byte[] bytes = Constants.Encoding.GetBytes(data);
			var ow = new ObjectWriter(repo);
			ObjectId id = ow.WriteBlob(bytes);
			try
			{
				parse(id);
				Assert.Fail("Object " + id.Name + " should not exist in test repository");
			}
			catch (MissingObjectException)
			{
				// Ok
			}

			return revWalk.lookupBlob(id);
		}
	}
}