// Copyright (c) 2010 AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under MIT X11 license (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

using ICSharpCode.NRefactory.Utils;

namespace ICSharpCode.NRefactory.TypeSystem.Implementation
{
	/// <summary>
	/// Simple <see cref="IProjectContent"/> implementation that stores the list of classes/namespaces.
	/// Synchronization is implemented using a <see cref="ReaderWriterLockSlim"/>.
	/// </summary>
	/// <remarks>
	/// Compared with <see cref="TypeStorage"/>, this class adds support for the IProjectContent interface,
	/// for partial classes, and for multi-threading.
	/// </remarks>
	public class SimpleProjectContent : AbstractAnnotatable, IProjectContent
	{
		readonly TypeStorage types = new TypeStorage();
		readonly ReaderWriterLockSlim readerWriterLock = new ReaderWriterLockSlim();
		readonly Dictionary<string, IParsedFile> fileDict = new Dictionary<string, IParsedFile>(Platform.FileNameComparer);
		
		#region AssemblyAttributes
		readonly List<IAttribute> assemblyAttributes = new List<IAttribute>(); // mutable assembly attribute storage
		readonly List<IAttribute> moduleAttributes = new List<IAttribute>();
		
		volatile IAttribute[] readOnlyAssemblyAttributes = {}; // volatile field with copy for reading threads
		volatile IAttribute[] readOnlyModuleAttributes = {};
		
		/// <inheritdoc/>
		public IList<IAttribute> AssemblyAttributes {
			get { return readOnlyAssemblyAttributes;  }
		}
		
		/// <inheritdoc/>
		public IList<IAttribute> ModuleAttributes {
			get { return readOnlyModuleAttributes;  }
		}
		
		static bool AddRemoveAttributes(ICollection<IAttribute> removedAttributes, ICollection<IAttribute> addedAttributes,
		                                List<IAttribute> attributeStorage)
		{
			// API uses ICollection instead of IEnumerable to discourage users from evaluating
			// the list inside the lock (this method is called inside the write lock)
			// [[not an issue anymore; the user now passes IParsedFile]]
			bool hasChanges = false;
			if (removedAttributes != null && removedAttributes.Count > 0) {
				if (attributeStorage.RemoveAll(removedAttributes.Contains) > 0)
					hasChanges = true;
			}
			if (addedAttributes != null) {
				attributeStorage.AddRange(addedAttributes);
				hasChanges = true;
			}
			return hasChanges;
		}
		
		void AddRemoveAssemblyAttributes(ICollection<IAttribute> removedAttributes, ICollection<IAttribute> addedAttributes)
		{
			if (AddRemoveAttributes(removedAttributes, addedAttributes, assemblyAttributes))
				readOnlyAssemblyAttributes = assemblyAttributes.ToArray();
		}
		
		void AddRemoveModuleAttributes(ICollection<IAttribute> removedAttributes, ICollection<IAttribute> addedAttributes)
		{
			if (AddRemoveAttributes(removedAttributes, addedAttributes, moduleAttributes))
				readOnlyModuleAttributes = moduleAttributes.ToArray();
		}
		#endregion
		
		#region AddType
		void AddType(ITypeDefinition typeDefinition)
		{
			if (typeDefinition == null)
				throw new ArgumentNullException("typeDefinition");
			if (typeDefinition.ProjectContent != this)
				throw new ArgumentException("Cannot add a type definition that belongs to another project content");
			
			// TODO: handle partial classes
			types.UpdateType(typeDefinition);
		}
		#endregion
		
		#region RemoveType
		void RemoveType(ITypeDefinition typeDefinition)
		{
			types.RemoveType (typeDefinition); // <- Daniel: Correct ?
		}
		#endregion
		
		#region UpdateProjectContent
		/// <summary>
		/// Removes types and attributes from oldFile from the project, and adds those from newFile.
		/// </summary>
		/// <remarks>
		/// The update is done inside a write lock; when other threads access this project content
		/// from within a <c>using (Synchronize())</c> block, they will not see intermediate (inconsistent) state.
		/// </remarks>
		public void UpdateProjectContent(IParsedFile oldFile, IParsedFile newFile)
		{
			if (oldFile != null && newFile != null) {
				if (!Platform.FileNameComparer.Equals(oldFile.FileName, newFile.FileName))
					throw new ArgumentException("When both oldFile and newFile are specified, they must use the same file name.");
			}
			readerWriterLock.EnterWriteLock();
			try {
				if (oldFile != null) {
					foreach (var element in oldFile.TopLevelTypeDefinitions) {
						RemoveType(element);
					}
					if (newFile == null) {
						fileDict.Remove(oldFile.FileName);
					}
				}
				if (newFile != null) {
					foreach (var element in newFile.TopLevelTypeDefinitions) {
						AddType(element);
					}
					fileDict[newFile.FileName] = newFile;
				}
				AddRemoveAssemblyAttributes(oldFile != null ? oldFile.AssemblyAttributes : null,
				                            newFile != null ? newFile.AssemblyAttributes : null);
				
				AddRemoveModuleAttributes(oldFile != null ? oldFile.ModuleAttributes : null,
				                          newFile != null ? newFile.ModuleAttributes : null);
			} finally {
				readerWriterLock.ExitWriteLock();
			}
		}
		#endregion
		
		#region IProjectContent implementation
		public ITypeDefinition GetTypeDefinition(string nameSpace, string name, int typeParameterCount, StringComparer nameComparer)
		{
			readerWriterLock.EnterReadLock();
			try {
				return types.GetTypeDefinition(nameSpace, name, typeParameterCount, nameComparer);
			} finally {
				readerWriterLock.ExitReadLock();
			}
		}
		
		public IEnumerable<ITypeDefinition> GetTypes()
		{
			readerWriterLock.EnterReadLock();
			try {
				// make a copy with ToArray() for thread-safe access
				return types.GetTypes().ToArray();
			} finally {
				readerWriterLock.ExitReadLock();
			}
		}
		
		public IEnumerable<ITypeDefinition> GetTypes(string nameSpace, StringComparer nameComparer)
		{
			readerWriterLock.EnterReadLock();
			try {
				// make a copy with ToArray() for thread-safe access
				return types.GetTypes(nameSpace, nameComparer).ToArray();
			} finally {
				readerWriterLock.ExitReadLock();
			}
		}
		
		public IEnumerable<string> GetNamespaces()
		{
			readerWriterLock.EnterReadLock();
			try {
				// make a copy with ToArray() for thread-safe access
				return types.GetNamespaces().ToArray();
			} finally {
				readerWriterLock.ExitReadLock();
			}
		}
		
		public string GetNamespace(string nameSpace, StringComparer nameComparer)
		{
			readerWriterLock.EnterReadLock();
			try {
				return types.GetNamespace(nameSpace, nameComparer);
			} finally {
				readerWriterLock.ExitReadLock();
			}
		}
		
		public IParsedFile GetFile(string fileName)
		{
			readerWriterLock.EnterReadLock();
			try {
				IParsedFile file;
				if (fileDict.TryGetValue(fileName, out file))
					return file;
				else
					return null;
			} finally {
				readerWriterLock.ExitReadLock();
			}
		}
		
		public IEnumerable<IParsedFile> Files {
			get {
				readerWriterLock.EnterReadLock();
				try {
					return fileDict.Values.ToArray();
				} finally {
					readerWriterLock.ExitReadLock();
				}
			}
		}
		#endregion
		
		#region Synchronization
		public CacheManager CacheManager {
			get { return null; }
		}
		
		public ISynchronizedTypeResolveContext Synchronize()
		{
			// don't acquire the lock on OutOfMemoryException etc.
			ISynchronizedTypeResolveContext sync = new ReadWriteSynchronizedTypeResolveContext(types, readerWriterLock);
			readerWriterLock.EnterReadLock();
			return sync;
		}
		
		sealed class ReadWriteSynchronizedTypeResolveContext : ProxyTypeResolveContext, ISynchronizedTypeResolveContext
		{
			ReaderWriterLockSlim readerWriterLock;
			
			public ReadWriteSynchronizedTypeResolveContext(ITypeResolveContext target, ReaderWriterLockSlim readerWriterLock)
				: base(target)
			{
				this.readerWriterLock = readerWriterLock;
			}
			
			public void Dispose()
			{
				if (readerWriterLock != null) {
					readerWriterLock.ExitReadLock();
					readerWriterLock = null;
				}
			}
			
			public override ISynchronizedTypeResolveContext Synchronize()
			{
				// nested Synchronize() calls don't need any locking
				return new ReadWriteSynchronizedTypeResolveContext(target, null);
			}
		}
		#endregion
	}
}
