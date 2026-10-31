// AssemblyInfo.InternalsVisibleTo.External.cs: grants to assemblies OUTSIDE this repository.
//
// Written by hand. `repo-conventions grants` writes this file only when it is missing, and never
// touches it after. Directory.Build.targets links it into every project that links
// AssemblyInfo.InternalsVisibleTo.cs, so a name here sees the internals of every such project.
//
// A name is an ASSEMBLY name. Most family assemblies are unprefixed ("AppServices"), and a grant
// to the root namespace instead ("Bennewitz.Ninja.AppServices") compiles, ships and grants
// nothing. Some do carry the prefix (FileServer's, Geo.Core's): `repo-conventions check` notes
// every prefixed name, and a line ending `// repo-conventions: assembly name` marks one checked.
// An internal another repository uses is a promise: change it only together with a release of
// that repository. See docs/repository-conventions.md in Bennewitz.Ninja.Templates.
//
// [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AppServices")]
