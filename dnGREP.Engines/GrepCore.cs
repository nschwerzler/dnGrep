using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using NLog;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using dnGREP.Common;
using dnGREP.Engines;

namespace dnGREP.Common
{
	public class GrepCore
	{
		private GrepEngineInitParams searchParams = new GrepEngineInitParams();

		public GrepEngineInitParams SearchParams
		{
			get { return searchParams; }
			set { searchParams = value; }
		}
		
		private static Logger logger = LogManager.GetCurrentClassLogger();
		private List<GrepSearchResult> searchResults = new List<GrepSearchResult>();
		public delegate void SearchProgressHandler(object sender, ProgressStatus files);
        public event SearchProgressHandler ProcessedFile = delegate { };
		public class ProgressStatus
		{
			public ProgressStatus(int processed, List<GrepSearchResult> results, string fileName)
			{
				ProcessedFiles = processed;
				SearchResults = results;
                FileName = fileName;
			}
			public int ProcessedFiles;
			public List<GrepSearchResult> SearchResults;
            public string FileName;
		}
	
		/// <summary>
		/// Searches folder for files whose content matches regex
		/// </summary>
		/// <param name="files">Files to search in. If one of the files does not exist or is open, it is skipped.</param>
		/// <param name="searchRegex">Regex pattern</param>
		/// <returns>List of results. If nothing is found returns empty list</returns>
		public List<GrepSearchResult> Search(IEnumerable<string> files, SearchType searchType, string searchPattern, GrepSearchOption searchOptions, int codePage)
		{
			List<GrepSearchResult> searchResults = new List<GrepSearchResult>();

			if (files == null)
				return searchResults;

            Utils.CancelSearch = false;

            if (searchPattern == null || searchPattern.Trim() == "")
            {
                foreach (string file in files)
                {
                    string fname = file;// Path.GetFileName(file);
                    ProcessedFile(this, new ProgressStatus(searchResults.Count, null, fname));

                    searchResults.Add(new GrepSearchResult(file, searchPattern, null, Encoding.Default));
                    if ((searchOptions & GrepSearchOption.StopAfterFirstMatch) == GrepSearchOption.StopAfterFirstMatch)
                        break;
                    if (Utils.CancelSearch)
                        break;
                }

                if (ProcessedFile != null)
                {
                    ProcessedFile(this, new ProgressStatus(searchResults.Count, searchResults, null));
                }

                return searchResults;
            }
            else
            {
                int processedFiles = 0;

                try
                {
                    foreach (string file in files)
                    {
                        string fname = file;// Path.GetFileName(file);
                        ProcessedFile(this, new ProgressStatus(processedFiles, null, fname));
                        try
                        {
                            IGrepEngine engine = GrepEngineFactory.GetSearchEngine(file, SearchParams);

                            processedFiles++;

                            Encoding encoding = null;
                            if (codePage == -1)
                                encoding = Utils.GetFileEncoding(file);
                            else
                                encoding = Encoding.GetEncoding(codePage);


                            if (Utils.CancelSearch)
                            {
                                return searchResults;
                            }

                            List<GrepSearchResult> fileSearchResults = engine.Search(file, searchPattern, searchType, searchOptions, encoding);

                            if (fileSearchResults != null && fileSearchResults.Count > 0)
                            {
                                searchResults.AddRange(fileSearchResults);
                            }

                            if (ProcessedFile != null)
                            {
                                ProcessedFile(this, new ProgressStatus(processedFiles, fileSearchResults, fname));
                            }

                        }
                        catch (Exception ex)
                        {
                            logger.Log<Exception>(LogLevel.Error, ex.Message, ex);
                            searchResults.Add(new GrepSearchResult(file, searchPattern, ex.Message, false));
                            if (ProcessedFile != null)
                            {
                                List<GrepSearchResult> _results = new List<GrepSearchResult>();
                                _results.Add(new GrepSearchResult(file, searchPattern, ex.Message, false));
                                ProcessedFile(this, new ProgressStatus(processedFiles, _results, fname));
                            }
                        }

                        if ((searchOptions & GrepSearchOption.StopAfterFirstMatch) == GrepSearchOption.StopAfterFirstMatch && searchResults.Count > 0)
                            break;
                    }
                }
                finally
                {
                    GrepEngineFactory.UnloadEngines();
                }

                return searchResults;
            }
		}

		public int Replace(IEnumerable<string> files, SearchType searchType, string searchPattern, string replacePattern, GrepSearchOption searchOptions, int codePage)
		{
			string tempFolder = Utils.GetTempFolder();

			if (files == null || !Directory.Exists(tempFolder))
				return 0;

            replacePattern = Utils.ReplaceSpecialCharacters(replacePattern);

			int processedFiles = 0;
            Utils.CancelSearch = false;
            string tempFileName = null;

			try
			{
				foreach (string file in files)
				{
                    string fname = file; // Path.GetFileName(file);
                    ProcessedFile(this, new ProgressStatus(processedFiles, null, fname));
                    
                    tempFileName = Path.Combine(tempFolder, Path.GetFileName(file));
					IGrepEngine engine = GrepEngineFactory.GetReplaceEngine(file, searchParams);

					try
					{
						processedFiles++;
						// Copy file					
						Utils.CopyFile(file, tempFileName, true);
                        Utils.DeleteFile(file);

						Encoding encoding = null;
						if (codePage == -1)
							encoding = Utils.GetFileEncoding(tempFileName);
						else
							encoding = Encoding.GetEncoding(codePage);


                        if (Utils.CancelSearch)
						{
							break;
						}

						if (!engine.Replace(tempFileName, file, searchPattern, replacePattern, searchType, searchOptions, encoding))
						{
							throw new ApplicationException("Replace failed for file: " + file);
						}

                        if (!Utils.CancelSearch && ProcessedFile != null)
							ProcessedFile(this, new ProgressStatus(processedFiles, null, fname));


						File.SetAttributes(file, File.GetAttributes(tempFileName));

                        if (Utils.CancelSearch)
						{
							// Replace the file
							Utils.DeleteFile(file);
							Utils.CopyFile(tempFileName, file, true);
							break;
						}
					}
					catch (Exception ex)
					{
						logger.Log<Exception>(LogLevel.Error, ex.Message, ex);
						try
						{
							// Replace the file
							if (File.Exists(tempFileName) && File.Exists(file))
							{
								Utils.DeleteFile(file);
								Utils.CopyFile(tempFileName, file, true);
							}
						}
						catch
						{
							// DO NOTHING
						}
						return -1;
					}
                    finally
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(tempFileName))
                                Utils.DeleteFile(tempFileName);
                        }
                        catch
                        {
                            // DO NOTHING
                        }
                    }
				}
			}
			finally
			{
				GrepEngineFactory.UnloadEngines();
			}

			return processedFiles;
		}

		public bool Undo(string folderPath)
		{
			string tempFolder = Utils.GetTempFolder();
			if (!Directory.Exists(tempFolder))
			{
				logger.Error("Failed to undo replacement as temporary directory was removed.");
				return false;
			}
			try
			{
				Utils.CopyFiles(tempFolder, folderPath, null, null);
				return true;
			}
			catch (Exception ex)
			{
				logger.Log<Exception>(LogLevel.Error, "Failed to undo replacement", ex);
				return false;
			}
		}		
	}
}
