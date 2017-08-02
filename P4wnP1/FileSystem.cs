using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Security;

namespace P4wnP1
{
    public class FileSystem
    {
        public static String pwd()
        {
            return Directory.GetCurrentDirectory();
        }

        public static String cd(String targetDir)
        {
            try
            {
                Directory.SetCurrentDirectory(targetDir);
                return Directory.GetCurrentDirectory();
            }
            catch (PathTooLongException e)
            {
                return String.Format("cd not possible: invalid target directory '{0}', path too long", targetDir);
            }
            catch (SecurityException e)
            {
                return String.Format("cd not possible: no permissions for '{0}'", targetDir);
            }
            catch (UnauthorizedAccessException e)
            {
                return String.Format("cd not possible: no permissions for '{0}'", targetDir);
            }
            catch (FileNotFoundException e)
            {
                return String.Format("cd not possible: invalid target directory '{0}', path not found", targetDir);
            }
            catch (DirectoryNotFoundException e)
            {
                return String.Format("cd not possible: invalid target directory '{0}', path not found", targetDir);
            }
            catch (IOException e)
            {
                return String.Format("cd not possible: Maybe {0} is a file", targetDir);
            }
            catch (ArgumentException e)
            {
                return String.Format("cd not possible: invalid target directory '{0}'", targetDir);
            }
        }

        public static String[] ls(String dir)
        {
            if ((dir == null) || (dir.Length == 0)) dir = ".";
            try
            {
                String[] rs = Directory.GetFileSystemEntries(dir);
                for (int i = 0; i < rs.Length; i++) rs[i] = Path.GetFileName(rs[i]); //trim to filename only
                return rs;
            }
            catch (PathTooLongException e)
            {
                return new String[] { String.Format("ls not possible: invalid target directory '{0}', path too long", dir) };
            }
            catch (UnauthorizedAccessException e)
            {
                return new String[] { String.Format("ls not possible: no permissions for '{0}'", dir) };
            }
            catch (DirectoryNotFoundException e)
            {
                return new String[] { String.Format("ls not possible: invalid target directory '{0}' not found", dir) };
            }
            catch (IOException e)
            {
                return new String[] { "ls not possible: IOError" };
            }
            catch (ArgumentException e)
            {
                return new String[] { String.Format("ls not possible: invalid target directory '{0}'", dir)};
            }
        }

    }
}
