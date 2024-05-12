using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using Ink;
using Ink.Parsed;

namespace InkLocaliser
{
    public class Localiser {

        private static string TAG_LOC = "loc:";

        public class Options {
            public bool retagAll = false;
            public bool debugRetagFiles = true; // Write retags to .ink.txt, not just .ink
        }
        private Options _options;

        protected struct TagInsert {
            public Text text;
            public string locID;
        }

        private HashSet<string> _inkFiles = new();
        private IFileHandler _fileHandler = new DefaultFileHandler();
        private bool _inkParseErrors = false;
        private HashSet<string> _filesVisited = new();
        private OrderedDictionary _strings = new();
        private Dictionary<string, List<TagInsert>> _filesTagsToInsert = new();

        public Localiser(Options? options = null) {
            _options = options ?? new Options();
        }

        public void AddFile(string inkFile) {
            _inkFiles.Add(inkFile);
        }

        public bool Run() {
            foreach(var inkFile in _inkFiles) {
                
                var content = _fileHandler.LoadInkFileContents(inkFile);
                if (content==null)
                    return false;

                InkParser parser = new InkParser(content, inkFile, OnError, _fileHandler);

                var story = parser.Parse();
                if (_inkParseErrors) {
                    Console.Error.WriteLine($"Error parsing ink file.");
                    return false;
                }

                if (!ProcessStory(story))
                    return false;
            }

            if (!InsertTagsToFiles())
                return false;

            return true;
        }

        private bool ProcessStory(Story story) {
                
            HashSet<string> newFileIDs = new();

            // ---- Find all the things we should localise ----
            List<Text> validTextObjects = new List<Text>();
            int lastLineNumber = -1;
            foreach(var text in story.FindAll<Text>())
            {
                // Just a newline? Ignore.
                if (text.text.Trim()=="")
                    continue;
                
                // If it's a tag, ignore.
                if (IsTextTag(text))
                    continue;

                // Is this inside some code? In which case we can't do anything with that.
                if (text.parent is VariableAssignment ||
                    text.parent is StringExpression) {
                    continue;
                }

                // More than one text chunk on a line? We only deal with individual lines of stuff.
                if (lastLineNumber == text.debugMetadata.startLineNumber) {
                    Console.Error.WriteLine($"Error in line {lastLineNumber} - two chunks of text when localiser can only work with one per line.");
                    return false;
                }
                lastLineNumber = text.debugMetadata.startLineNumber;

                // Have we already visited this source file i.e. is it in an include we've seen before?
                // If so, skip.
                string fileID = System.IO.Path.GetFileNameWithoutExtension(text.debugMetadata.fileName);
                if (_filesVisited.Contains(fileID)) {
                    continue;
                }
                newFileIDs.Add(fileID);


                Console.WriteLine(text.text+"-"+text.parent.GetType());
                    
                validTextObjects.Add(text);
            }

            if (newFileIDs.Count>0)
                _filesVisited.UnionWith(newFileIDs);

            // ---- Sort out IDs ----
            // Now we've got our list of text, let's iterate through looking for IDs, and create them when they're missing.
            // IDs are stored as tags in the form #loc:file_knot_stitch_xxxx

            foreach(var text in validTextObjects) {

                string fileName = text.debugMetadata.fileName;
                string fileID = System.IO.Path.GetFileNameWithoutExtension(fileName);
                string pathPrefix = fileID+"_";  
                string locPrefix = MakeLocPrefix(text);
                string uid = GenerateID();  
                string locID = pathPrefix+locPrefix+uid;

                // Does the source already have a #loc: tag?
                string? locTag = FindLocTagID(text);
                // Replace either if there's no tag or if we're forcing a retag
                if (locTag==null || _options.retagAll)
                {
                    if (!_filesTagsToInsert.ContainsKey(fileName))
                        _filesTagsToInsert[fileName] = new List<TagInsert>();
                    var insert = new TagInsert
                    {
                        text = text,
                        locID = locID
                    };
                    _filesTagsToInsert[fileName].Add(insert);
                }

                _strings[locID] = text.text;
            }

            return true;
        }

        private bool InsertTagsToFiles() {

            foreach (var (fileName, workList) in _filesTagsToInsert) {
                if (workList.Count==0)
                    continue;
                
                Console.WriteLine($"Updating IDs in file: {fileName}");

                if (!InsertTagsToFile(fileName, workList))
                    return false;
            }
            return true;
        }

        private bool InsertTagsToFile(string fileName, List<TagInsert> workList) {

            try {             
                string filePath = _fileHandler.ResolveInkFilename(fileName);
                string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);

                foreach(var item in workList) {
                    // Tag
                    string newTag = $"#{TAG_LOC}{item.locID}";

                    // Find out where we're supposed to do the insert.
                    int lineNumber = item.text.debugMetadata.endLineNumber-1;
                    string oldLine = lines[lineNumber];

                    if (oldLine.Contains($"#{TAG_LOC}")) {
                        // Is there already a tag called Loc: in there? In which case, we just want to replace that.

                        // Regex pattern to find "#loc:" followed by any alphanumeric characters or underscores
                        string pattern = $@"(#{TAG_LOC})\w+";

                        // Replace the matched text
                        string newLine = Regex.Replace(oldLine, pattern, $"{newTag}");
                        lines[lineNumber] = newLine;
                    }
                    else
                    {
                        // No tag, add a new one.
                        int charPos = item.text.debugMetadata.endCharacterNumber-1;

                        
                        if (!Char.IsWhiteSpace(oldLine[charPos-1]))
                            newTag = " "+newTag;
                        if (oldLine.Length>charPos && oldLine[charPos]=='#')
                            newTag = newTag+" ";
                        string newLine = oldLine.Insert(charPos, newTag);
                        lines[lineNumber] = newLine;
                    }
                    
                    //Console.WriteLine($"Trying to add new tag for text:{item.text.text} at position {charPos} line:\n{lines[lineNumber]}");
                }

                string output = String.Join("\n", lines);
                string outputFilePath = filePath;
                if (_options.debugRetagFiles)
                    outputFilePath += ".txt";
                File.WriteAllText(outputFilePath, output, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error replacing tags in {fileName}: " + ex.Message);
                return false;
            }
        }

        // Checking it's a tag. Is there a StartTag earlier in the parent content?        
        private bool IsTextTag(Text text) {

            int inTag = 0;

            foreach (var sibling in text.parent.content) {
                if (sibling==text) {
                    break;
                }
                if (sibling is Tag) {
                    var tag = (Tag)sibling;
                    if (tag.isStart)
                        inTag++;
                    else
                        inTag--;
                }
            }

            return (inTag>0);
        }

        private string? FindLocTagID(Text text) {
            List<string> tags = GetTagsAfterText(text);
            if (tags.Count>0) {
                foreach(var tag in tags) {
                    if (tag.StartsWith(TAG_LOC)) {
                        return tag.Substring(4);
                    }
                }
            }
            return null;
        }

        private List<string> GetTagsAfterText(Text text) {
        
            var tags = new List<string>();

            bool afterText = false;
            int inTag = 0;

            foreach (var sibling in text.parent.content) {
                
                if (sibling==text) {
                    afterText = true;
                    continue;
                }
                if (!afterText)
                    continue;
                
                if (sibling is Tag) {
                    var tag = (Tag)sibling;
                    if (tag.isStart)
                        inTag++;
                    else
                        inTag--;
                    continue;
                }

                if ((inTag>0) && (sibling is Text)) {
                    tags.Add(((Text)sibling).text);
                } 
            }
            return tags;
        }

        // Constructs a prefix from knot / stitch
        private string MakeLocPrefix(Text text) {

            string prefix = "";
            foreach (var obj in text.ancestry) {
                if (obj is Knot)
                    prefix+=((Knot)obj).name+"_";
                if (obj is Stitch)
                    prefix+=((Stitch)obj).name+"_";
            }

            return prefix;
        }

        private static Random _random = new Random();
        private static string GenerateID(int length=4)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            char[] stringChars = new char[length];
            for (int i = 0; i < length; i++)
            {
                stringChars[i] = chars[_random.Next(chars.Length)];
            }
            return new String(stringChars);
        }

        void OnError(string message, ErrorType type)
        {
            _inkParseErrors = true;
            Console.Error.WriteLine("Ink Parse Error: "+message);
        }
    }
}