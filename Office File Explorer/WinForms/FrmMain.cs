﻿// Open XML SDK refs
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Office2013.Word;
using DocumentFormat.OpenXml.Packaging;

// App refs
using Office_File_Explorer.Helpers;
using Office_File_Explorer.WinForms;

// .NET refs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using System.Drawing;
using System.Xml.Schema;

using File = System.IO.File;
using Color = System.Drawing.Color;

namespace Office_File_Explorer
{
    public partial class FrmMain : Form
    {
        // global variables
        private string findText;
        private string replaceText;
        private string fromChangeTemplate;

        // openmcdf
        private FileStream fs;

        // corrupt doc legacy
        private static string StrCopiedFileName = string.Empty;
        private static string StrOfficeApp = string.Empty;
        private static char PrevChar = Strings.chLessThan;
        private bool IsRegularXmlTag;
        private bool IsFixed;
        private static string FixedFallback = string.Empty;
        private static string StrExtension = string.Empty;
        private static string StrDestFileName = string.Empty;
        static StringBuilder sbNodeBuffer = new StringBuilder();

        // temp files
        public static string tempFileReadOnly, tempFilePackageViewer;

        // lists
        private static List<string> corruptNodes = new List<string>();
        private static List<string> pParts = new List<string>();
        private List<string> oNumIdList = new List<string>();

        // part viewer globals
        public List<PackagePart> pkgParts = new List<PackagePart>();

        // package is for viewing of contents only
        public Package package;

        public bool hasXmlError;

        public enum OpenXmlInnerFileTypes
        {
            Word,
            Excel,
            PowerPoint,
            XML,
            Image,
            Binary,
            Video,
            Audio,
            Text,
            Other
        }

        // enums
        public enum LogInfoType { ClearAndAdd, TextOnly, InvalidFile, LogException, EmptyCount };

        public FrmMain()
        {
            InitializeComponent();

            // update title with version
            this.Text = Strings.oAppTitle + Strings.wMinusSign + Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>().Version;

            // make sure the log file is created
            if (!File.Exists(Strings.fLogFilePath))
            {
                File.Create(Strings.fLogFilePath);
            }
        }

        #region Class Properties

        public string FindTextProperty
        {
            set => findText = value;
        }

        public string ReplaceTextProperty
        {
            set => replaceText = value;
        }

        public string DefaultTemplate
        {
            set => fromChangeTemplate = value;
        }
        #endregion

        #region Functions

        /// <summary>
        /// tempFileReadOnly is used for the View Contents feature
        /// tempFilePackageViewer is used for the main form part viewer
        /// changes made in the part viewer are then saved back to the toolstripstatusfilepath
        /// </summary>
        public void TempFileSetup()
        {
            try
            {
                tempFileReadOnly = Path.GetTempFileName().Replace(".tmp", ".docx");
                File.Copy(toolStripStatusLabelFilePath.Text, tempFileReadOnly, true);

                tempFilePackageViewer = Path.GetTempFileName().Replace(".tmp", ".docx");
                File.Copy(toolStripStatusLabelFilePath.Text, tempFilePackageViewer, true);

            }
            catch (Exception ex)
            {
                FileUtilities.WriteToLog(Strings.fLogFilePath, "Temp File Setup Error:");
                FileUtilities.WriteToLog(Strings.fLogFilePath, ex.Message);
            }
        }

        /// <summary>
        /// handle when user clicks File | Close
        /// </summary>
        public void FileClose()
        {
            DisableCustomUIIcons();
            DisableModifyUI();
            DisableUI();
            package?.Close();
            pkgParts?.Clear();
            tvFiles.Nodes.Clear();
            toolStripStatusLabelFilePath.Text = Strings.wHeadingBegin;
            toolStripStatusLabelDocType.Text = Strings.wHeadingBegin;
            rtbDisplay.Clear();
            fileToolStripMenuItemClose.Enabled = false;
        }

        /// <summary>
        /// disable app feature related buttons
        /// </summary>
        public void DisableUI()
        {
            toolStripButtonViewContents.Enabled = false;
            toolStripButtonFixDoc.Enabled = false;
            editToolStripMenuFindReplace.Enabled = false;
            editToolStripMenuItemModifyContents.Enabled = false;
            editToolStripMenuItemRemoveCustomDocProps.Enabled = false;
            editToolStripMenuItemRemoveCustomXml.Enabled = false;
            excelSheetViewerToolStripMenuItem.Enabled = false;
            toolStripButtonModify.Enabled = false;

            if (package is null)
            {
                fileToolStripMenuItemClose.Enabled = false;
            }
        }

        /// <summary>
        /// disable app feature related buttons
        /// </summary>
        public void EnableUI()
        {
            toolStripButtonViewContents.Enabled = true;
            toolStripButtonFixDoc.Enabled = true;
            editToolStripMenuFindReplace.Enabled = true;
            editToolStripMenuItemModifyContents.Enabled = true;
            editToolStripMenuItemRemoveCustomDocProps.Enabled = true;
            editToolStripMenuItemRemoveCustomXml.Enabled = true;
            toolStripButtonModify.Enabled = true;
            fileToolStripMenuItemClose.Enabled = true;
            toolStripButtonModify.Enabled = true;
        }

        public void CopyAllItems()
        {
            try
            {
                if (rtbDisplay.Text.Length == 0) { return; }
                StringBuilder buffer = new StringBuilder();
                foreach (string s in rtbDisplay.Lines)
                {
                    buffer.Append(s);
                    buffer.Append('\n');
                }

                Clipboard.SetText(buffer.ToString());
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "BtnCopyOutput Error", ex.Message);
            }
        }

        public void OpenEncryptedOfficeDocument(string fileName, bool enableCommit)
        {
            try
            {
                fs = new FileStream(fileName, FileMode.Open, enableCommit ? FileAccess.ReadWrite : FileAccess.Read);
                FrmEncryptedFile cForm = new FrmEncryptedFile(fs, true)
                {
                    Owner = this
                };

                if (cForm.IsDisposed)
                {
                    return;
                }
                else
                {
                    cForm.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "OpenEncryptedOfficeDocument Error", ex.Message);
            }
        }

        public void DisplayInvalidFileFormatError()
        {
            rtbDisplay.AppendText("Unable to open file, possible causes are:");
            rtbDisplay.AppendText(" - file corruption");
            rtbDisplay.AppendText(" - file encrypted");
            rtbDisplay.AppendText(" - file password protected");
            rtbDisplay.AppendText(" - binary Office Document (View file contents with Tools -> Structured Storage Viewer)");
        }

        /// <summary>
        /// majority of open file logic is here
        /// </summary>
        public void OpenOfficeDocument()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                OpenFileDialog fDialog = new OpenFileDialog
                {
                    Title = "Select Office Open Xml File.",
                    Filter = "Open XML Files | *.docx; *.dotx; *.docm; *.dotm; *.xlsx; *.xlsm; *.xlst; *.xltm; *.pptx; *.pptm; *.potx; *.potm|" +
                             "Binary Office Documents | *.doc; *.dot; *.xls; *.xlt; *.ppt; *.pot",
                    RestoreDirectory = true,
                    InitialDirectory = @"%userprofile%"
                };

                if (fDialog.ShowDialog() == DialogResult.OK)
                {
                    toolStripStatusLabelFilePath.Text = fDialog.FileName.ToString();
                    if (!File.Exists(toolStripStatusLabelFilePath.Text))
                    {
                        LogInformation(LogInfoType.InvalidFile, Strings.fileDoesNotExist, string.Empty);
                    }
                    else
                    {
                        rtbDisplay.Clear();

                        // if the file doesn't start with PK, we can stop trying to process it
                        if (!FileUtilities.IsZipArchiveFile(toolStripStatusLabelFilePath.Text))
                        {
                            DisplayInvalidFileFormatError();
                            DisableUI();
                            structuredStorageViewerToolStripMenuItem.Enabled = true;
                        }
                        else
                        {
                            // if the file does start with PK, check if it fails in the SDK
                            if (OpenWithSdk(toolStripStatusLabelFilePath.Text))
                            {
                                // set the file type
                                toolStripStatusLabelDocType.Text = StrOfficeApp;

                                // populate the parts
                                PopulatePackageParts();

                                // check if any zip items are corrupt
                                if (Properties.Settings.Default.CheckZipItemCorrupt == true && toolStripStatusLabelDocType.Text == Strings.oAppWord)
                                {
                                    if (Office.IsZippedFileCorrupt(toolStripStatusLabelFilePath.Text))
                                    {
                                        rtbDisplay.AppendText("Warning - One of the zipped items is corrupt.");
                                    }
                                }

                                // setup temp files
                                TempFileSetup();

                                // clear the previous doc if there was one
                                tvFiles.Nodes.Clear();
                                rtbDisplay.Clear();
                                package?.Close();
                                pkgParts?.Clear();

                                // populate the treeview
                                package = Package.Open(toolStripStatusLabelFilePath.Text, FileMode.Open, FileAccess.ReadWrite);

                                TreeNode tRoot = new TreeNode();
                                tRoot.Text = toolStripStatusLabelFilePath.Text;

                                // update file icon
                                if (GetFileType(toolStripStatusLabelFilePath.Text) == OpenXmlInnerFileTypes.Word)
                                {
                                    tvFiles.SelectedImageIndex = 0;
                                    tvFiles.ImageIndex = 0;
                                }
                                else if (GetFileType(toolStripStatusLabelFilePath.Text) == OpenXmlInnerFileTypes.Excel)
                                {
                                    tvFiles.SelectedImageIndex = 2;
                                    tvFiles.ImageIndex = 2;
                                }
                                else if (GetFileType(toolStripStatusLabelFilePath.Text) == OpenXmlInnerFileTypes.PowerPoint)
                                {
                                    tvFiles.SelectedImageIndex = 1;
                                    tvFiles.ImageIndex = 1;
                                }

                                // update inner file icon, need to update both the selected and normal image index
                                foreach (PackagePart part in package.GetParts())
                                {
                                    tRoot.Nodes.Add(part.Uri.ToString());

                                    if (GetFileType(part.Uri.ToString()) == OpenXmlInnerFileTypes.XML)
                                    {
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].ImageIndex = 3;
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].SelectedImageIndex = 3;
                                    }
                                    else if (GetFileType(part.Uri.ToString()) == OpenXmlInnerFileTypes.Image)
                                    {
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].ImageIndex = 4;
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].SelectedImageIndex = 4;
                                    }
                                    else if (GetFileType(part.Uri.ToString()) == OpenXmlInnerFileTypes.Word)
                                    {
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].ImageIndex = 0;
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].SelectedImageIndex = 0;
                                    }
                                    else if (GetFileType(part.Uri.ToString()) == OpenXmlInnerFileTypes.Excel)
                                    {
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].ImageIndex = 2;
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].SelectedImageIndex = 2;
                                    }
                                    else if (GetFileType(part.Uri.ToString()) == OpenXmlInnerFileTypes.PowerPoint)
                                    {
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].ImageIndex = 1;
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].SelectedImageIndex = 1;
                                    }
                                    else
                                    {
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].ImageIndex = 5;
                                        tRoot.Nodes[tRoot.Nodes.Count - 1].SelectedImageIndex = 5;
                                    }

                                    pkgParts.Add(part);
                                }

                                tvFiles.Nodes.Add(tRoot);
                                tvFiles.ExpandAll();
                                DisableModifyUI();
                            }
                            else
                            {
                                // if it failed the SDK, disable all buttons except the fix corrupt doc button
                                DisableUI();
                                if (toolStripStatusLabelFilePath.Text.EndsWith(Strings.docxFileExt))
                                {
                                    toolStripButtonFixCorruptDoc.Enabled = true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // user cancelled dialog, disable the UI and go back to the form
                    DisableUI();
                    DisableModifyUI();
                    toolStripStatusLabelFilePath.Text = Strings.wHeadingBegin;
                    toolStripStatusLabelDocType.Text = Strings.wHeadingBegin;
                    return;
                }
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "File Open Error: ", ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        public OpenXmlInnerFileTypes GetFileType(string path)
        {
            switch (Path.GetExtension(path))
            {
                case ".docx":
                case ".dotx":
                case ".dotm":
                case ".docm":
                    return OpenXmlInnerFileTypes.Word;
                case ".xlsx":
                case ".xlsm":
                case ".xltm":
                case ".xltx":
                case ".xlsb":
                    return OpenXmlInnerFileTypes.Excel;
                case ".pptx":
                case ".pptm":
                case ".ppsx":
                case ".ppsm":
                case ".potx":
                case ".potm":
                    return OpenXmlInnerFileTypes.PowerPoint;
                case ".jpeg":
                case ".jpg":
                case ".bmp":
                case ".png":
                case ".gif":
                case ".emf":
                case ".wmf":
                    return OpenXmlInnerFileTypes.Image;
                case ".xml":
                case ".rels":
                    return OpenXmlInnerFileTypes.XML;
                case ".mp4":
                case ".avi":
                case ".wmv":
                case ".mov":
                    return OpenXmlInnerFileTypes.Video;
                case ".mp3":
                case ".wav":
                case ".wma":
                    return OpenXmlInnerFileTypes.Audio;
                case ".txt":
                    return OpenXmlInnerFileTypes.Text;
                case ".bin":
                case ".sigs":
                case ".odttf":
                    return OpenXmlInnerFileTypes.Binary;
                default:
                    return OpenXmlInnerFileTypes.Binary;
            }
        }

        public void LogInformation(LogInfoType type, string output, string ex)
        {
            switch (type)
            {
                case LogInfoType.ClearAndAdd:
                    rtbDisplay.Clear();
                    rtbDisplay.AppendText(output);
                    break;
                case LogInfoType.InvalidFile:
                    rtbDisplay.Clear();
                    rtbDisplay.AppendText(Strings.invalidFile);
                    break;
                case LogInfoType.LogException:
                    rtbDisplay.Clear();
                    rtbDisplay.AppendText(output + "\r\n" + ex);
                    FileUtilities.WriteToLog(Strings.fLogFilePath, output);
                    FileUtilities.WriteToLog(Strings.fLogFilePath, ex);
                    break;
                case LogInfoType.EmptyCount:
                    rtbDisplay.AppendText(Strings.wNone);
                    break;
                default:
                    rtbDisplay.AppendText(output);
                    break;
            }
        }

        /// <summary>
        /// add each package part to a global list
        /// </summary>
        public void PopulatePackageParts()
        {
            pParts.Clear();
            using (FileStream zipToOpen = new FileStream(toolStripStatusLabelFilePath.Text, FileMode.Open, FileAccess.Read))
            {
                using (ZipArchive archive = new ZipArchive(zipToOpen, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry zae in archive.Entries)
                    {
                        pParts.Add(zae.FullName + Strings.wColonBuffer + FileUtilities.SizeSuffix(zae.Length));
                    }
                    pParts.Sort();
                }
            }
        }

        /// <summary>
        /// open a file in the SDK, any failure means it is not a valid docx
        /// </summary>
        /// <param name="file">the path to the initial fix attempt</param>
        public bool OpenWithSdk(string file)
        {
            string body = string.Empty;
            bool fSuccess = false;

            try
            {
                Cursor = Cursors.WaitCursor;

                // add opensettings to get around the fix malformed uri issue
                var openSettings = new OpenSettings()
                {
                    RelationshipErrorHandlerFactory = package => { return new UriRelationshipErrorHandler(); }
                };

                if (FileUtilities.GetAppFromFileExtension(file) == Strings.oAppWord)
                {
                    using (WordprocessingDocument document = WordprocessingDocument.Open(file, true, openSettings))
                    {
                        // try to get the localname of the document.xml file, if it fails, it is not a Word file
                        StrOfficeApp = Strings.oAppWord;
                        body = document.MainDocumentPart.Document.LocalName;
                        fSuccess = true;
                    }
                }
                else if (FileUtilities.GetAppFromFileExtension(file) == Strings.oAppExcel)
                {
                    using (SpreadsheetDocument document = SpreadsheetDocument.Open(file, true, openSettings))
                    {
                        // try to get the localname of the workbook.xml and file if it fails, its not an Excel file
                        StrOfficeApp = Strings.oAppExcel;
                        body = document.WorkbookPart.Workbook.LocalName;
                        fSuccess = true;
                    }
                }
                else if (FileUtilities.GetAppFromFileExtension(file) == Strings.oAppPowerPoint)
                {
                    using (PresentationDocument document = PresentationDocument.Open(file, true, openSettings))
                    {
                        // try to get the presentation.xml local name, if it fails it is not a PPT file
                        StrOfficeApp = Strings.oAppPowerPoint;
                        body = document.PresentationPart.Presentation.LocalName;
                        fSuccess = true;
                    }
                }
                else
                {
                    // file is corrupt or not an Office document
                    StrOfficeApp = Strings.oAppUnknown;
                    LogInformation(LogInfoType.ClearAndAdd, "Invalid File", string.Empty);
                }
            }
            catch (InvalidOperationException ioe)
            {
                LogInformation(LogInfoType.LogException, Strings.errorOpenWithSDK, ioe.Message);
                LogInformation(LogInfoType.LogException, Strings.errorOpenWithSDK, ioe.StackTrace);
            }
            catch (Exception ex)
            {
                // if the file failed to open in the sdk, it is invalid or corrupt and we need to stop opening
                LogInformation(LogInfoType.LogException, Strings.errorOpenWithSDK, ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            return fSuccess;
        }

        /// <summary>
        /// output content to the listbox
        /// </summary>
        /// <param name="output">the list of content to display</param>
        /// <param name="type">the type of content to display</param>
        public StringBuilder DisplayListContents(List<string> output, string type)
        {
            StringBuilder sb = new StringBuilder();
            // add title text for the contents
            sb.AppendLine(Strings.wHeadingBegin + type + Strings.wHeadingEnd);

            // no content to display
            if (output.Count == 0)
            {
                sb.AppendLine(string.Empty);
                return sb;
            }

            // if we have any values, display them
            foreach (string s in output)
            {
                sb.AppendLine(Strings.wTripleSpace + s);
            }

            sb.AppendLine(string.Empty);
            return sb;
        }

        /// <summary>
        /// update the node buffer for BtnFixCorruptDoc_Click logic
        /// </summary>
        /// <param name="input"></param>
        public static void Node(char input)
        {
            sbNodeBuffer.Append(input);
        }

        /// <summary>
        /// this function loops through all nodes parsed out from Step 1 in BtnFixCorruptDoc_Click
        /// check each node and add fallback tags only to the list
        /// </summary>
        /// <param name="originalText"></param>
        public static void GetAllNodes(string originalText)
        {
            bool isFallback = false;
            var fallback = new List<string>();

            foreach (string o in corruptNodes)
            {
                if (o == Strings.txtFallbackStart)
                {
                    isFallback = true;
                }

                if (isFallback)
                {
                    fallback.Add(o);
                }

                if (o == Strings.txtFallbackEnd)
                {
                    isFallback = false;
                }
            }

            ParseOutFallbackTags(fallback, originalText);
        }

        /// <summary>
        /// we should only have a list of fallback start tags, end tags and each tag in between
        /// the idea is to combine these start/middle/end tags into a long string
        /// then they can be replaced with an empty string
        /// </summary>
        /// <param name="input"></param>
        /// <param name="originalText"></param>
        public static void ParseOutFallbackTags(List<string> input, string originalText)
        {
            var fallbackTagsAppended = new List<string>();
            StringBuilder sbFallback = new StringBuilder();

            foreach (string o in input)
            {
                switch (o.ToString())
                {
                    case Strings.txtFallbackStart:
                        sbFallback.Append(o);
                        continue;
                    case Strings.txtFallbackEnd:
                        sbFallback.Append(o);
                        fallbackTagsAppended.Add(sbFallback.ToString());
                        sbFallback.Clear();
                        continue;
                    default:
                        sbFallback.Append(o);
                        continue;
                }
            }

            sbFallback.Clear();

            // loop each item in the list and remove it from the document
            originalText = fallbackTagsAppended.Aggregate(originalText, (current, o) => current.Replace(o.ToString(), string.Empty));

            // each set of fallback tags should now be removed from the text
            // set it to the global variable so we can add it back into document.xml
            FixedFallback = originalText;
        }

        public static List<string> CfpList(CustomFilePropertiesPart part)
        {
            List<string> val = new List<string>();
            foreach (CustomDocumentProperty cdp in part.RootElement.Cast<CustomDocumentProperty>())
            {
                val.Add(cdp.Name + Strings.wColonBuffer + cdp.InnerText);
            }
            return val;
        }

        /// <summary>
        /// append strings like "Fixed" or "Copy" to the file name
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="TextToAdd"></param>
        /// <returns></returns>
        public static string AddTextToFileName(string fileName, string TextToAdd)
        {
            string dir = Path.GetDirectoryName(fileName) + "\\";
            StrExtension = Path.GetExtension(fileName);
            string newFileName = dir + Path.GetFileNameWithoutExtension(fileName) + TextToAdd + StrExtension;
            return newFileName;
        }

        public static void DeleteTempFiles()
        {
            try
            {
                if (File.Exists(tempFilePackageViewer))
                {
                    File.Delete(tempFilePackageViewer);
                }

                if (File.Exists(tempFileReadOnly))
                {
                    File.Delete(tempFileReadOnly);
                }
            }
            catch (Exception ex)
            {
                FileUtilities.WriteToLog(Strings.fLogFilePath, "DeleteTempFiles Error: " + ex.Message);
            }
        }

        /// <summary>
        /// cleanup before the app exits
        /// </summary>
        public static void AppExitWork()
        {
            try
            {
                if (Properties.Settings.Default.DeleteCopiesOnExit == true && File.Exists(StrCopiedFileName))
                {
                    File.Delete(StrCopiedFileName);
                }

                Properties.Settings.Default.Save();
                DeleteTempFiles();
            }
            catch (Exception ex)
            {
                FileUtilities.WriteToLog(Strings.fLogFilePath, "App Exit Error: " + ex.Message);
            }
            finally
            {
                Application.Exit();
            }
        }

        #endregion

        #region Button Events

        private void OpenToolStripMenuItem_Click(object sender, EventArgs e)
        {
            structuredStorageViewerToolStripMenuItem.Enabled = false;
            EnableUI();
            EnableModifyUI();
            OpenOfficeDocument();

            if (toolStripStatusLabelDocType.Text == Strings.oAppExcel)
            {
                excelSheetViewerToolStripMenuItem.Enabled = true;
            }
        }

        private void SettingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FrmSettings form = new FrmSettings();
            form.Show();
        }

        private void BatchFileProcessingToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FrmBatch bFrm = new FrmBatch(package)
            {
                Owner = this
            };
            bFrm.ShowDialog();
        }

        private void FrmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            package?.Close();
            AppExitWork();
        }

        private void ExitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            package.Close();
            AppExitWork();
        }

        private void FeedbackToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppUtilities.PlatformSpecificProcessStart(Strings.helpLocation);
        }

        private void AboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FrmAbout frm = new FrmAbout();
            frm.ShowDialog(this);
            frm.Dispose();
        }

        private void OpenErrorLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppUtilities.PlatformSpecificProcessStart(Strings.fLogFilePath);
        }

        public List<string> CustomDocPropsList(CustomFilePropertiesPart cfp)
        {
            List<string> tempCfp = new List<string>();

            if (cfp is null)
            {
                LogInformation(LogInfoType.EmptyCount, Strings.wCustomDocProps, string.Empty);
                return tempCfp;
            }

            int count = 0;

            foreach (string v in CfpList(cfp))
            {
                count++;
                tempCfp.Add(count + Strings.wPeriod + v);
            }

            if (count == 0)
            {
                LogInformation(LogInfoType.EmptyCount, Strings.wCustomDocProps, string.Empty);
            }

            return tempCfp;
        }

        private void ClipboardViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FrmClipboardViewer cFrm = new FrmClipboardViewer()
            {
                Owner = this
            };
            cFrm.ShowDialog();
        }

        private void CopySelectedLineToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (rtbDisplay.Text.Length == 0)
                {
                    Clipboard.SetText(rtbDisplay.SelectedText);
                }
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "BtnCopyLineOutput Error", ex.Message);
            }
        }

        private void CopyAllLinesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CopyAllItems();
        }

        private void Base64DecoderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FrmBase64 b64Frm = new FrmBase64()
            {
                Owner = this
            };
            b64Frm.ShowDialog();
        }

        private void openFileBackupFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppUtilities.PlatformSpecificProcessStart(Path.GetDirectoryName(Application.LocalUserAppDataPath));
        }

        private void structuredStorageViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenEncryptedOfficeDocument(toolStripStatusLabelFilePath.Text, true);
        }

        private void excelSheetViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var f = new FrmSheetViewer(tempFileReadOnly))
            {
                var result = f.ShowDialog();
            }
        }

        public void EnableModifyUI()
        {
            rtbDisplay.ReadOnly = false;
            rtbDisplay.BackColor = SystemColors.Window;
            toolStripButtonSave.Enabled = true;
        }

        public void DisableModifyUI()
        {
            rtbDisplay.ReadOnly = true;
            rtbDisplay.BackColor = SystemColors.Control;
            toolStripButtonSave.Enabled = false;
        }

        public void EnableCustomUIIcons()
        {
            toolStripButtonGenerateCallback.Enabled = true;
            toolStripButtonValidateXml.Enabled = true;
            toolStripDropDownButtonInsert.Enabled = true;
            //toolStripButtonInsertIcon.Enabled = true;
        }

        public void DisableCustomUIIcons()
        {
            toolStripDropDownButtonInsert.Enabled = false;
            toolStripButtonSave.Enabled = false;
            toolStripButtonGenerateCallback.Enabled = false;
            toolStripButtonValidateXml.Enabled = false;
            //toolStripButtonInsertIcon.Enabled = false;
        }

        private void ShowError(string errorText)
        {
            MessageBox.Show(this, errorText, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static void ValidationCallback(object sender, ValidationEventArgs args)
        {
            if (args.Severity == XmlSeverityType.Warning)
            {
                Console.Write("WARNING: ");
            }
            else if (args.Severity == XmlSeverityType.Error)
            {
                Console.Write("ERROR: ");
            }
        }

        /// <summary>
        /// use the schema to validate the xml
        /// </summary>
        /// <param name="showValidMessage"></param>
        /// <returns></returns>
        public bool ValidateXml(bool showValidMessage)
        {
            if (rtbDisplay.Text == null || rtbDisplay.Text.Length == 0)
            {
                return false;
            }

            rtbDisplay.SuspendLayout();

            try
            {
                XmlTextReader xtr = new XmlTextReader(@".\Schemas\customui14.xsd");
                XmlSchema schema = XmlSchema.Read(xtr, ValidationCallback);

                XmlDocument xmlDoc = new XmlDocument();

                if (schema == null)
                {
                    return false;
                }

                xmlDoc.Schemas.Add(schema);
                xmlDoc.LoadXml(rtbDisplay.Text);

                if (xmlDoc.DocumentElement.NamespaceURI.ToString() != schema.TargetNamespace)
                {
                    StringBuilder errorText = new StringBuilder();
                    errorText.Append("Unknown Namespace".Replace("|1", xmlDoc.DocumentElement.NamespaceURI.ToString()));
                    errorText.Append("\n" + "CustomUI Namespace".Replace("|1", schema.TargetNamespace));

                    ShowError(errorText.ToString());
                    return false;
                }

                hasXmlError = false;
                xmlDoc.Validate(XmlValidationEventHandler);
            }
            catch (XmlException ex)
            {
                ShowError("Invalid Xml" + "\n" + ex.Message);
                return false;
            }

            rtbDisplay.ResumeLayout();

            if (!hasXmlError)
            {
                if (showValidMessage)
                {
                    MessageBox.Show(this, "Valid Xml", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return true;
            }
            return false;
        }

        private void XmlValidationEventHandler(object sender, ValidationEventArgs e)
        {
            lock (this)
            {
                hasXmlError = true;
            }
            MessageBox.Show(this, e.Message, e.Severity.ToString(), MessageBoxButtons.OK,
                (e.Severity == XmlSeverityType.Error ? MessageBoxIcon.Error : MessageBoxIcon.Warning));
        }

        private void AddPart(XMLParts partType)
        {
            OfficePart newPart = CreateCustomUIPart(partType);
            TreeNode partNode = ConstructPartNode(newPart);
            TreeNode currentNode = tvFiles.Nodes[0];
            if (currentNode == null) return;

            tvFiles.SuspendLayout();
            currentNode.Nodes.Add(partNode);
            rtbDisplay.Text = string.Empty;
            tvFiles.SelectedNode = partNode;
            tvFiles.ResumeLayout();
        }

        private TreeNode ConstructPartNode(OfficePart part)
        {
            TreeNode node = new TreeNode(part.Name);
            node.Tag = part.PartType;
            node.ImageIndex = 3;
            node.SelectedImageIndex = 3;
            return node;
        }

        private OfficePart RetrieveCustomPart(XMLParts partType)
        {
            if (pParts == null || pParts.Count == 0) return null;

            OfficePart oPart;

            foreach (PackagePart pp in pkgParts)
            {
                if (pp.Uri.ToString() == Strings.CustomUI14PartRelType)
                {
                    return oPart = new OfficePart(pp, XMLParts.RibbonX14, Strings.CustomUI14PartRelType);
                }
                else if (pp.Uri.ToString() == Strings.CustomUIPartRelType)
                {
                    return oPart = new OfficePart(pp, XMLParts.RibbonX14, Strings.CustomUIPartRelType);
                }
            }

            return null;
        }

        private OfficePart CreateCustomUIPart(XMLParts partType)
        {
            string relativePath;
            string relType;

            switch (partType)
            {
                case XMLParts.RibbonX12:
                    relativePath = "/customUI/customUI.xml";
                    relType = Strings.CustomUIPartRelType;
                    break;
                case XMLParts.RibbonX14:
                    relativePath = "/customUI/customUI14.xml";
                    relType = Strings.CustomUI14PartRelType;
                    break;
                case XMLParts.QAT12:
                    relativePath = "/customUI/qat.xml";
                    relType = Strings.QATPartRelType;
                    break;
                default:
                    return null;
            }

            Uri customUIUri = new Uri(relativePath, UriKind.Relative);
            PackageRelationship relationship = package.CreateRelationship(customUIUri, TargetMode.Internal, relType);

            OfficePart part = null;
            if (!package.PartExists(customUIUri))
            {
                part = new OfficePart(package.CreatePart(customUIUri, "application/xml"), partType, relationship.Id);
            }
            else
            {
                part = new OfficePart(package.GetPart(customUIUri), partType, relationship.Id);
            }

            return part;
        }

        private void tvFiles_AfterSelect(object sender, TreeViewEventArgs e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;

                if (GetFileType(e.Node.Text) == OpenXmlInnerFileTypes.XML)
                {
                    // customui files have additional editing options
                    if (e.Node.Text.EndsWith("customUI.xml") || e.Node.Text.EndsWith("customUI14.xml"))
                    {
                        EnableCustomUIIcons();
                    }
                    else
                    {
                        DisableCustomUIIcons();
                    }

                    // load file contents
                    foreach (PackagePart pp in pkgParts)
                    {
                        if (pp.Uri.ToString() == tvFiles.SelectedNode.Text)
                        {
                            using (StreamReader sr = new StreamReader(pp.GetStream()))
                            {
                                string contents = sr.ReadToEnd();

                                // convert the contents to indented xml
                                XmlDocument doc = new XmlDocument();
                                doc.LoadXml(contents);
                                StringBuilder sb = new StringBuilder();
                                XmlWriterSettings settings = new XmlWriterSettings
                                {
                                    Indent = true,
                                    IndentChars = "  ",
                                    NewLineChars = "\r\n",
                                    NewLineHandling = NewLineHandling.Replace
                                };
                                using (XmlWriter writer = XmlWriter.Create(sb, settings))
                                {
                                    doc.Save(writer);
                                }

                                tvFiles.SuspendLayout();
                                rtbDisplay.Text = sb.ToString();

                                // format the xml for colors
                                string pattern = @"</?(?<tagName>[a-zA-Z0-9_:\-]+)" + @"(\s+(?<attName>[a-zA-Z0-9_:\-]+)(?<attValue>(=""[^""]+"")?))*\s*/?>";
                                foreach (Match m in Regex.Matches(rtbDisplay.Text, pattern))
                                {
                                    rtbDisplay.Select(m.Index, m.Length);
                                    rtbDisplay.SelectionColor = Color.Blue;

                                    var tagName = m.Groups["tagName"].Value;
                                    rtbDisplay.Select(m.Groups["tagName"].Index, m.Groups["tagName"].Length);
                                    rtbDisplay.SelectionColor = Color.DarkRed;

                                    var attGroup = m.Groups["attName"];
                                    if (attGroup is not null)
                                    {
                                        var atts = attGroup.Captures;
                                        for (int i = 0; i < atts.Count; i++)
                                        {
                                            rtbDisplay.Select(atts[i].Index, atts[i].Length);
                                            rtbDisplay.SelectionColor = Color.Red;
                                        }
                                    }
                                }

                                tvFiles.ResumeLayout();
                                return;
                            }
                        }
                    }
                }
                else if (GetFileType(e.Node.Text) == OpenXmlInnerFileTypes.Image)
                {
                    foreach (PackagePart pp in pkgParts)
                    {
                        if (pp.Uri.ToString() == tvFiles.SelectedNode.Text)
                        {
                            // need to implement non-bitmap images
                            if (pp.Uri.ToString().EndsWith(".emf"))
                            {
                                rtbDisplay.Text = "No Viewer For File Type";
                                return;
                            }

                            Stream imageSource = pp.GetStream();
                            Image image = Image.FromStream(imageSource);
                            using (var f = new FrmDisplayOutput(image))
                            {
                                var result = f.ShowDialog();
                            }
                            return;
                        }
                    }
                }
                else if (GetFileType(e.Node.Text) == OpenXmlInnerFileTypes.Binary)
                {
                    foreach (PackagePart pp in pkgParts)
                    {
                        if (pp.Uri.ToString() == tvFiles.SelectedNode.Text)
                        {
                            using (Stream ppStream = pp.GetStream())
                            {
                                byte[] binData = new byte[ppStream.Length];
                                ppStream.Read(binData, 0, binData.Length);
                                rtbDisplay.Text = ppStream.ToString();
                            }
                            return;
                        }
                    }
                }
                else
                {
                    rtbDisplay.Text = "No Viewer For File Type";
                }
            }
            catch (Exception ex)
            {
                rtbDisplay.Text = "Error: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void toolStripButtonValidateXml_Click(object sender, EventArgs e)
        {
            ValidateXml(true);
        }

        private void toolStripButtonGenerateCallback_Click(object sender, EventArgs e)
        {
            // if there is no callback , then there is no point in generating the callback code
            if (rtbDisplay.Text == null || rtbDisplay.Text.Length == 0)
            {
                return;
            }

            // if the xml is not valid, then there is no point in generating the callback code
            if (!ValidateXml(false))
            {
                return;
            }

            // if we have valid xml, then generate the callback code
            try
            {
                XmlDocument customUI = new XmlDocument();
                customUI.LoadXml(rtbDisplay.Text);
                StringBuilder callbacks = CallbackBuilder.GenerateCallback(customUI);
                callbacks.Append("}");

                // display the callbacks
                using (var f = new FrmDisplayOutput(callbacks, true))
                {
                    f.Text = "Callback Code";
                    var result = f.ShowDialog();
                }

                if (callbacks == null || callbacks.Length == 0)
                {
                    MessageBox.Show(this, "No callbacks found", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void office2010CustomUIPartToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AddPart(XMLParts.RibbonX14);
        }

        private void office2007CustomUIPartToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AddPart(XMLParts.RibbonX12);
        }

        private void customOutspaceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            rtbDisplay.Text = Strings.xmlCustomOutspace;
        }

        private void customTabToolStripMenuItem_Click(object sender, EventArgs e)
        {
            rtbDisplay.Text = Strings.xmlCustomTab;
        }

        private void excelCustomTabToolStripMenuItem_Click(object sender, EventArgs e)
        {
            rtbDisplay.Text = Strings.xmlExcelCustomTab;
        }

        private void repurposeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            rtbDisplay.Text = Strings.xmlRepurpose;
        }

        private void wordGroupOnInsertTabToolStripMenuItem_Click(object sender, EventArgs e)
        {
            rtbDisplay.Text = Strings.xmlWordGroupInsertTab;
        }

        private void toolStripButtonInsertIcon_Click(object sender, EventArgs e)
        {
            OpenFileDialog fDialog = new OpenFileDialog
            {
                Title = "Insert Custom Icon",
                Filter = "Supported Icons | *.ico; *.bmp; *.png; *.jpg; *.jpeg; *.tif;| All Files | *.*;",
                RestoreDirectory = true,
                InitialDirectory = @"%userprofile%"
            };

            if (fDialog.ShowDialog() == DialogResult.OK)
            {
                XMLParts partType = XMLParts.RibbonX14;
                OfficePart part = RetrieveCustomPart(partType);
                tvFiles.SuspendLayout();

                foreach (string fileName in (sender as OpenFileDialog).FileNames)
                {
                    try
                    {
                        string id = XmlConvert.EncodeName(Path.GetFileNameWithoutExtension(fileName));
                        Stream imageStream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        Image image = Image.FromStream(imageStream, true, true);

                        // The file is a valid image at this point.
                        id = part.AddImage(fileName, id);

                        Debug.Assert(id != null, "Cannot create image part.");
                        if (id == null) continue;

                        imageStream.Close();

                        TreeNode imageNode = new TreeNode(id);
                        imageNode.ImageKey = "_" + id;
                        imageNode.SelectedImageKey = imageNode.ImageKey;
                        imageNode.Tag = partType;

                        tvFiles.ImageList.Images.Add(imageNode.ImageKey, image);
                        tvFiles.Nodes.Add(imageNode);
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex.Message);
                        continue;
                    }
                }

                tvFiles.ResumeLayout();
            }
        }

        private void toolStripButtonModify_Click(object sender, EventArgs e)
        {
            EnableModifyUI();
        }

        private void toolStripButtonSave_Click(object sender, EventArgs e)
        {
            foreach (PackagePart pp in pkgParts)
            {
                if (pp.Uri.ToString() == tvFiles.SelectedNode.Text)
                {
                    MemoryStream ms = new MemoryStream();
                    using (TextWriter tw = new StreamWriter(ms))
                    {
                        tw.Write(rtbDisplay.Text);
                        tw.Flush();

                        ms.Position = 0;
                        Stream partStream = pp.GetStream(FileMode.OpenOrCreate, FileAccess.Write);
                        partStream.SetLength(0);
                        ms.WriteTo(partStream);
                    }

                    break;
                }
            }

            package.Flush();

            // update ui
            DisableModifyUI();
        }

        private void toolStripButtonViewContents_Click(object sender, EventArgs e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                StringBuilder sb = new StringBuilder();

                // display file contents based on user selection
                if (StrOfficeApp == Strings.oAppWord)
                {
                    sb.Append(DisplayListContents(Word.LstContentControls(tempFileReadOnly), Strings.wContentControls));
                    sb.Append(DisplayListContents(Word.LstTables(tempFileReadOnly), Strings.wTables));
                    sb.Append(DisplayListContents(Word.LstStyles(tempFileReadOnly), Strings.wStyles));
                    sb.Append(DisplayListContents(Word.LstHyperlinks(tempFileReadOnly), Strings.wHyperlinks));
                    sb.Append(DisplayListContents(Word.LstListTemplates(tempFileReadOnly, false), Strings.wListTemplates));
                    sb.Append(DisplayListContents(Word.LstFonts(tempFileReadOnly), Strings.wFonts));
                    sb.Append(DisplayListContents(Word.LstRunFonts(tempFileReadOnly), Strings.wRunFonts));
                    sb.Append(DisplayListContents(Word.LstFootnotes(tempFileReadOnly), Strings.wFootnotes));
                    sb.Append(DisplayListContents(Word.LstEndnotes(tempFileReadOnly), Strings.wEndnotes));
                    sb.Append(DisplayListContents(Word.LstDocProps(tempFileReadOnly), Strings.wDocProps));
                    sb.Append(DisplayListContents(Word.LstBookmarks(tempFileReadOnly), Strings.wBookmarks));
                    sb.Append(DisplayListContents(Word.LstFieldCodes(tempFileReadOnly), Strings.wFldCodes));
                    sb.Append(DisplayListContents(Word.LstFieldCodesInHeader(tempFileReadOnly), " ** Header Field Codes **"));
                    sb.Append(DisplayListContents(Word.LstFieldCodesInFooter(tempFileReadOnly), " ** Footer Field Codes **"));
                    sb.Append(DisplayListContents(Word.LstTables(tempFileReadOnly), Strings.wTables));
                }
                else if (StrOfficeApp == Strings.oAppExcel)
                {
                    sb.Append(DisplayListContents(Excel.GetLinks(tempFileReadOnly, true), Strings.wLinks));
                    sb.Append(DisplayListContents(Excel.GetComments(tempFileReadOnly), Strings.wComments));
                    sb.Append(DisplayListContents(Excel.GetHyperlinks(tempFileReadOnly), Strings.wHyperlinks));
                    sb.Append(DisplayListContents(Excel.GetSheetInfo(tempFileReadOnly), Strings.wWorksheetInfo));
                    sb.Append(DisplayListContents(Excel.GetSharedStrings(tempFileReadOnly), Strings.wSharedStrings));
                    sb.Append(DisplayListContents(Excel.GetDefinedNames(tempFileReadOnly), Strings.wDefinedNames));
                    sb.Append(DisplayListContents(Excel.GetConnections(tempFileReadOnly), Strings.wConnections));
                    sb.Append(DisplayListContents(Excel.GetHiddenRowCols(tempFileReadOnly), Strings.wHiddenRowCol));
                }
                else if (StrOfficeApp == Strings.oAppPowerPoint)
                {
                    sb.Append(DisplayListContents(PowerPoint.GetHyperlinks(tempFileReadOnly), Strings.wHyperlinks));
                    sb.Append(DisplayListContents(PowerPoint.GetComments(tempFileReadOnly), Strings.wComments));
                    sb.Append(DisplayListContents(PowerPoint.GetSlideText(tempFileReadOnly), Strings.wSlideText));
                    sb.Append(DisplayListContents(PowerPoint.GetSlideTitles(tempFileReadOnly), Strings.wSlideText));
                    sb.Append(DisplayListContents(PowerPoint.GetSlideTransitions(tempFileReadOnly), Strings.wSlideTransitions));
                    sb.Append(DisplayListContents(PowerPoint.GetFonts(tempFileReadOnly), Strings.wFonts));
                }

                // display selected Office features

                sb.Append(DisplayListContents(Office.GetEmbeddedObjectProperties(tempFileReadOnly, toolStripStatusLabelDocType.Text), Strings.wEmbeddedObjects));
                sb.Append(DisplayListContents(Office.GetShapes(tempFileReadOnly, toolStripStatusLabelDocType.Text), Strings.wShapes));
                sb.Append(DisplayListContents(pParts, Strings.wPackageParts));
                sb.Append(DisplayListContents(Office.GetSignatures(tempFileReadOnly, toolStripStatusLabelDocType.Text), Strings.wXmlSignatures));

                // validate the file and update custom file props
                if (toolStripStatusLabelDocType.Text == Strings.oAppWord)
                {
                    using (WordprocessingDocument myDoc = WordprocessingDocument.Open(tempFileReadOnly, false))
                    {
                        sb.Append(DisplayListContents(Office.DisplayValidationErrorInformation(myDoc), Strings.errorValidation));
                        sb.Append(DisplayListContents(CustomDocPropsList(myDoc.CustomFilePropertiesPart), Strings.wCustomDocProps));
                    }
                }
                else if (toolStripStatusLabelDocType.Text == Strings.oAppExcel)
                {
                    using (SpreadsheetDocument myDoc = SpreadsheetDocument.Open(tempFileReadOnly, false))
                    {
                        sb.Append(DisplayListContents(Office.DisplayValidationErrorInformation(myDoc), Strings.errorValidation));
                        sb.Append(DisplayListContents(CustomDocPropsList(myDoc.CustomFilePropertiesPart), Strings.wCustomDocProps));
                    }
                }
                else if (toolStripStatusLabelDocType.Text == Strings.oAppPowerPoint)
                {
                    using (PresentationDocument myDoc = PresentationDocument.Open(tempFileReadOnly, false))
                    {
                        sb.Append(DisplayListContents(Office.DisplayValidationErrorInformation(myDoc), Strings.errorValidation));
                        sb.Append(DisplayListContents(CustomDocPropsList(myDoc.CustomFilePropertiesPart), Strings.wCustomDocProps));
                    }
                }

                using (var f = new FrmDisplayOutput(sb, false))
                {
                    f.Text = "File Contents";
                    var result = f.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "ViewContents Error:", ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void toolStripButtonFixCorruptDoc_Click(object sender, EventArgs e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                StrDestFileName = AddTextToFileName(toolStripStatusLabelFilePath.Text, Strings.wFixedFileParentheses);
                bool isXmlException = false;
                string strDocText = string.Empty;
                IsFixed = false;
                rtbDisplay.Clear();

                if (StrExtension == Strings.docxFileExt)
                {
                    if ((File.GetAttributes(toolStripStatusLabelFilePath.Text) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        rtbDisplay.AppendText("ERROR: File is Read-Only.");
                        return;
                    }
                    else
                    {
                        File.Copy(toolStripStatusLabelFilePath.Text, StrDestFileName, true);
                    }
                }

                // bug in packaging API in .NET Core, need to break this fix into separate using blocks to get around the problem
                // 1. check for the xml corruption in document.xml
                using (Package package = Package.Open(StrDestFileName, FileMode.Open, FileAccess.Read))
                {
                    foreach (PackagePart part in package.GetParts())
                    {
                        if (part.Uri.ToString() == Strings.wdDocumentXml)
                        {
                            XmlDocument xdoc = new XmlDocument();

                            try
                            {
                                xdoc.Load(part.GetStream(FileMode.Open, FileAccess.Read));
                            }
                            catch (XmlException) // invalid xml found, try to fix the contents
                            {
                                isXmlException = true;
                            }
                        }
                    }
                }

                // 2. find any known bad sequences and create a string with those changes
                using (Package package = Package.Open(StrDestFileName, FileMode.Open, FileAccess.Read))
                {
                    if (isXmlException)
                    {
                        foreach (PackagePart part in package.GetParts())
                        {
                            if (part.Uri.ToString() == Strings.wdDocumentXml)
                            {
                                InvalidXmlTags invalid = new InvalidXmlTags();
                                string strDocTextBackup;

                                using (TextReader tr = new StreamReader(part.GetStream(FileMode.Open, FileAccess.Read)))
                                {
                                    strDocText = tr.ReadToEnd();
                                    strDocTextBackup = strDocText;

                                    foreach (string el in invalid.InvalidTags())
                                    {
                                        foreach (Match m in Regex.Matches(strDocText, el))
                                        {
                                            switch (m.Value)
                                            {
                                                case ValidXmlTags.StrValidMcChoice1:
                                                case ValidXmlTags.StrValidMcChoice2:
                                                case ValidXmlTags.StrValidMcChoice3:
                                                    break;

                                                case InvalidXmlTags.StrInvalidVshape:
                                                    // the original strvalidvshape fixes most corruptions, but there are
                                                    // some that are within a group so I added this for those rare situations
                                                    // where the v:group closing tag needs to be included
                                                    if (Properties.Settings.Default.FixGroupedShapes == true)
                                                    {
                                                        strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidVshapegroup);
                                                        rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                        rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidVshapegroup);
                                                    }
                                                    else
                                                    {
                                                        strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidVshape);
                                                        rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                        rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidVshape);
                                                    }
                                                    break;

                                                case InvalidXmlTags.StrInvalidOmathWps:
                                                    strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidomathwps);
                                                    rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                    rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidomathwps);
                                                    break;

                                                case InvalidXmlTags.StrInvalidOmathWpg:
                                                    strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidomathwpg);
                                                    rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                    rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidomathwpg);
                                                    break;

                                                case InvalidXmlTags.StrInvalidOmathWpc:
                                                    strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidomathwpc);
                                                    rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                    rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidomathwpc);
                                                    break;

                                                case InvalidXmlTags.StrInvalidOmathWpi:
                                                    strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidomathwpi);
                                                    rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                    rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidomathwpi);
                                                    break;

                                                default:
                                                    // default catch for "strInvalidmcChoiceRegEx" and "strInvalidFallbackRegEx"
                                                    // since the exact string will never be the same and always has different trailing tags
                                                    // we need to conditionally check for specific patterns
                                                    // the first if </mc:Choice> is to catch and replace the invalid mc:Choice tags
                                                    if (m.Value.Contains(Strings.txtMcChoiceTagEnd))
                                                    {
                                                        if (m.Value.Contains("<mc:Fallback id="))
                                                        {
                                                            // secondary check for a fallback that has an attribute.
                                                            // we don't allow attributes in a fallback
                                                            strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidMcChoice4);
                                                            rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                            rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidMcChoice4);
                                                            break;
                                                        }

                                                        // replace mc:choice and hold onto the tag that follows
                                                        strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrValidMcChoice3 + m.Groups[2].Value);
                                                        rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                        rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrValidMcChoice3 + m.Groups[2].Value);
                                                        break;
                                                    }
                                                    // the second if <w:pict/> is to catch and replace the invalid mc:Fallback tags
                                                    else if (m.Value.Contains("<w:pict/>"))
                                                    {
                                                        if (m.Value.Contains(Strings.txtFallbackEnd))
                                                        {
                                                            // if the match contains the closing fallback we just need to remove the entire fallback
                                                            // this will leave the closing AC and Run tags, which should be correct
                                                            strDocText = strDocText.Replace(m.Value, string.Empty);
                                                            rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                            rtbDisplay.AppendText(Strings.replacedWith + "Fallback tag deleted.");
                                                            break;
                                                        }

                                                        // if there is no closing fallback tag, we can replace the match with the omitFallback valid tags
                                                        // then we need to also add the trailing tag, since it's always different but needs to stay in the file
                                                        strDocText = strDocText.Replace(m.Value, ValidXmlTags.StrOmitFallback + m.Groups[2].Value);
                                                        rtbDisplay.AppendText(Strings.invalidTag + m.Value);
                                                        rtbDisplay.AppendText(Strings.replacedWith + ValidXmlTags.StrOmitFallback + m.Groups[2].Value);
                                                        break;
                                                    }
                                                    else
                                                    {
                                                        // leaving this open for future checks
                                                        break;
                                                    }
                                            }
                                        }
                                    }

                                    // remove all fallback tags is a 3 step process
                                    // Step 1. start by getting a list of all nodes/values in the document.xml file
                                    // Step 2. call GetAllNodes to add each fallback tag
                                    // Step 3. call ParseOutFallbackTags to remove each fallback
                                    if (Properties.Settings.Default.RemoveFallback == true)
                                    {
                                        CharEnumerator charEnum = strDocText.GetEnumerator();
                                        while (charEnum.MoveNext())
                                        {
                                            // keep track of previous char
                                            PrevChar = charEnum.Current;

                                            // opening tag
                                            switch (charEnum.Current)
                                            {
                                                case Strings.chLessThan:
                                                    // if we haven't hit a close, but hit another '<' char
                                                    // we are not a true open tag so add it like a regular char
                                                    if (sbNodeBuffer.Length > 0)
                                                    {
                                                        corruptNodes.Add(sbNodeBuffer.ToString());
                                                        sbNodeBuffer.Clear();
                                                    }
                                                    Node(charEnum.Current);
                                                    break;

                                                case Strings.chGreaterThan:
                                                    // there are 2 ways to close out a tag
                                                    // 1. self contained tag like <w:sz w:val="28"/>
                                                    // 2. standard xml <w:t>test</w:t>
                                                    // if previous char is '/', then we are an end tag
                                                    if (PrevChar == Strings.chBackslash || IsRegularXmlTag)
                                                    {
                                                        Node(charEnum.Current);
                                                        IsRegularXmlTag = false;
                                                    }
                                                    Node(charEnum.Current);
                                                    corruptNodes.Add(sbNodeBuffer.ToString());
                                                    sbNodeBuffer.Clear();
                                                    break;

                                                default:
                                                    // this is the second xml closing style, keep track of char
                                                    if (PrevChar == Strings.chLessThan && charEnum.Current == Strings.chBackslash)
                                                    {
                                                        IsRegularXmlTag = true;
                                                    }
                                                    Node(charEnum.Current);
                                                    break;
                                            }

                                            // cleanup
                                            charEnum.Dispose();
                                        }

                                        GetAllNodes(strDocText);
                                        strDocText = FixedFallback;
                                    }

                                    // if no changes were made, no corruptions were found and we can exit
                                    if (strDocText.Equals(strDocTextBackup))
                                    {
                                        rtbDisplay.AppendText(" ## No Corruption Found  ## ");
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }

                // 3. write the part with the changes into the new file
                using (Package package = Package.Open(StrDestFileName, FileMode.Open, FileAccess.ReadWrite))
                {
                    MemoryStream ms = new MemoryStream();

                    using (TextWriter tw = new StreamWriter(ms))
                    {
                        foreach (PackagePart part in package.GetParts())
                        {
                            if (part.Uri.ToString() == Strings.wdDocumentXml)
                            {
                                tw.Write(strDocText);
                                tw.Flush();

                                // write the part
                                ms.Position = 0;
                                Stream partStream = part.GetStream(FileMode.Open, FileAccess.Write);
                                partStream.SetLength(0);
                                ms.WriteTo(partStream);
                                IsFixed = true;
                            }
                        }
                    }
                }
            }
            catch (FileFormatException ffe)
            {
                DisplayInvalidFileFormatError();
                FileUtilities.WriteToLog(Strings.fLogFilePath, "Corrupt Doc Exception = " + ffe.Message);
            }
            catch (Exception ex)
            {
                rtbDisplay.Text = Strings.errorUnableToFixDocument + ex.Message;
                FileUtilities.WriteToLog(Strings.fLogFilePath, "Corrupt Doc Exception = " + ex.Message);
            }
            finally
            {
                // only delete destination file when there is an error
                // need to make sure the file stays when it is fixed
                if (IsFixed == false)
                {
                    // delete the copied file if it exists
                    if (File.Exists(StrDestFileName))
                    {
                        File.Delete(StrDestFileName);
                    }

                    LogInformation(LogInfoType.EmptyCount, Strings.wInvalidXml, string.Empty);
                }
                else
                {
                    // since we were able to attempt the fixes
                    // check if we can open in the sdk and confirm it was indeed fixed
                    if (OpenWithSdk(StrDestFileName))
                    {
                        rtbDisplay.AppendText("-------------------------------------------------------------" + Environment.NewLine + "Fixed Document Location: " + StrDestFileName);
                    }
                    else
                    {
                        rtbDisplay.AppendText("Unable to fix document");
                    }
                }

                // reset the globals
                IsFixed = false;
                IsRegularXmlTag = false;
                FixedFallback = string.Empty;
                StrExtension = string.Empty;
                StrDestFileName = string.Empty;
                PrevChar = Strings.chLessThan;
                Cursor = Cursors.Default;
            }
        }

        private void toolStripButtonFixDoc_Click(object sender, EventArgs e)
        {
            using (FrmFixDocument f = new FrmFixDocument(tempFilePackageViewer, toolStripStatusLabelFilePath.Text, toolStripStatusLabelDocType.Text))
            {
                f.ShowDialog();

                if (f.isFileFixed == true)
                {
                    if (f.corruptionChecked == "All")
                    {
                        int count = 0;
                        foreach (var s in f.featureFixed)
                        {
                            count++;
                            rtbDisplay.Text = count + Strings.wPeriod + s;
                        }
                    }
                    else
                    {
                        rtbDisplay.Text = "Corrupt " + f.corruptionChecked + " Found - Document Fixed";
                        rtbDisplay.Text = "Modified File Location = " + tempFilePackageViewer;
                    }

                    return;
                }
                else
                {
                    // if it wasn't cancelled, no corruption was found
                    // if it was cancelled, do nothing
                    if (f.corruptionChecked != Strings.wCancel)
                    {
                        LogInformation(LogInfoType.ClearAndAdd, "No Corruption Found", string.Empty);
                    }
                }
            }
        }

        private void editToolStripMenuFindReplace_Click(object sender, EventArgs e)
        {
            FrmSearchReplace srForm = new FrmSearchReplace()
            {
                Owner = this
            };
            srForm.ShowDialog();

            if (string.IsNullOrEmpty(findText) && string.IsNullOrEmpty(replaceText))
            {
                return;
            }

            Office.SearchAndReplace(toolStripStatusLabelFilePath.Text, findText, replaceText);
            LogInformation(LogInfoType.ClearAndAdd, "** Search and Replace Finished **", string.Empty);
        }

        /// <summary>
        /// append strings like "Fixed" or "Copy" to the file name
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="TextToAdd"></param>
        /// <returns></returns>
        public static string AddModifiedTextToFileName(string fileName)
        {
            string dir = Path.GetDirectoryName(fileName) + "\\";
            StrExtension = Path.GetExtension(fileName);
            string newFileName = dir + Path.GetFileNameWithoutExtension(fileName) + Strings.wModified + StrExtension;
            return newFileName;
        }

        private void editToolStripMenuItemModifyContents_Click(object sender, EventArgs e)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                if (StrOfficeApp == Strings.oAppWord)
                {
                    using (var f = new FrmWordModify())
                    {
                        DialogResult result = f.ShowDialog();

                        if (result == DialogResult.Cancel)
                        {
                            return;
                        }

                        Cursor = Cursors.WaitCursor;

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelHF)
                        {
                            if (Word.RemoveHeadersFooters(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Headers and Footers Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete headers and footers", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelComments)
                        {
                            if (Word.RemoveComments(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Comments Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete comments", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelEndnotes)
                        {
                            if (Word.RemoveEndnotes(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Endnotes Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete endnotes", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelFootnotes)
                        {
                            if (Word.RemoveFootnotes(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Footnotes Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete footnotes", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelOrphanLT)
                        {
                            oNumIdList = Word.LstListTemplates(tempFilePackageViewer, true);
                            foreach (object orphanLT in oNumIdList)
                            {
                                Word.RemoveListTemplatesNumId(tempFilePackageViewer, orphanLT.ToString());
                            }
                            LogInformation(LogInfoType.ClearAndAdd, "Unused List Templates Removed", string.Empty);
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelOrphanStyles)
                        {
                            DisplayListContents(Word.RemoveUnusedStyles(tempFilePackageViewer), Strings.wStyles);
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelHiddenTxt)
                        {
                            if (Word.DeleteHiddenText(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Hidden Text Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete hidden text", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelPgBrk)
                        {
                            if (Word.RemoveBreaks(tempFilePackageViewer))
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Page Breaks Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete Page Breaks", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.SetPrintOrientation)
                        {
                            FrmPrintOrientation pFrm = new FrmPrintOrientation(tempFilePackageViewer)
                            {
                                Owner = this
                            };
                            pFrm.ShowDialog();
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.AcceptRevisions)
                        {
                            foreach (var s in Word.AcceptRevisions(tempFilePackageViewer, Strings.allAuthors))
                            {
                                sb.AppendLine(s);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.ChangeDefaultTemplate)
                        {
                            bool isFileChanged = false;
                            string attachedTemplateId = "rId1";
                            string filePath = string.Empty;

                            using (WordprocessingDocument document = WordprocessingDocument.Open(tempFilePackageViewer, true))
                            {
                                DocumentSettingsPart dsp = document.MainDocumentPart.DocumentSettingsPart;

                                // if the external rel exists, we need to pull the rId and old uri
                                // we will be deleting this part and re-adding with the new uri
                                if (dsp.ExternalRelationships.Any())
                                {
                                    foreach (ExternalRelationship er in dsp.ExternalRelationships)
                                    {
                                        if (er.RelationshipType != null && er.RelationshipType == Strings.DocumentTemplatePartType)
                                        {
                                            // keep track of the existing rId for the template
                                            filePath = er.Uri.ToString();
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    // if the part does not exist, this is a Normal.dotm situation
                                    // path out to where it should be based on default install settings
                                    filePath = Strings.fNormalTemplatePath;

                                    if (!File.Exists(filePath))
                                    {
                                        // Normal.dotm path is not correct?
                                        LogInformation(LogInfoType.InvalidFile, "BtnChangeDefaultTemplate", "Invalid Attached Template Path - " + filePath);
                                        throw new Exception();
                                    }
                                }

                                // get the new template path from the user
                                FrmChangeDefaultTemplate ctFrm = new FrmChangeDefaultTemplate(FileUtilities.ConvertUriToFilePath(filePath))
                                {
                                    Owner = this
                                };
                                ctFrm.ShowDialog();

                                if (fromChangeTemplate == filePath || fromChangeTemplate is null || fromChangeTemplate == Strings.wCancel)
                                {
                                    // file path is the same or user closed without wanting changes, do nothing
                                    return;
                                }
                                else
                                {
                                    filePath = fromChangeTemplate;

                                    // delete the old part if it exists
                                    if (dsp.ExternalRelationships.Any())
                                    {
                                        dsp.DeleteExternalRelationship(attachedTemplateId);
                                        isFileChanged = true;
                                    }

                                    // if we aren't Normal, add a new part back in with the new path
                                    if (fromChangeTemplate != "Normal")
                                    {
                                        Uri newFilePath = new Uri(filePath);
                                        dsp.AddExternalRelationship(Strings.DocumentTemplatePartType, newFilePath, attachedTemplateId);
                                        isFileChanged = true;
                                    }
                                    else
                                    {
                                        // if we are changing to Normal, delete the attachtemplate id ref
                                        foreach (OpenXmlElement oe in dsp.Settings)
                                        {
                                            if (oe.ToString() == Strings.dfowAttachedTemplate)
                                            {
                                                oe.Remove();
                                                isFileChanged = true;
                                            }
                                        }
                                    }
                                }

                                if (isFileChanged)
                                {
                                    sb.AppendLine("** Attached Template Path Changed **");
                                    document.MainDocumentPart.Document.Save();
                                }
                                else
                                {
                                    sb.AppendLine("** No Changes Made To Attached Template **");
                                }
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.ConvertDocmToDocx)
                        {
                            string fNewName = Office.ConvertMacroEnabled2NonMacroEnabled(tempFilePackageViewer, Strings.oAppWord);
                            rtbDisplay.Clear();
                            if (fNewName != string.Empty)
                            {
                                sb.AppendLine(tempFilePackageViewer + Strings.convertedTo + fNewName);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.RemovePII)
                        {
                            using (WordprocessingDocument document = WordprocessingDocument.Open(tempFilePackageViewer, true))
                            {
                                if ((Word.HasPersonalInfo(document) == true) && Word.RemovePersonalInfo(document) == true)
                                {
                                    LogInformation(LogInfoType.ClearAndAdd, "PII Removed from file.", string.Empty);
                                }
                                else
                                {
                                    LogInformation(LogInfoType.EmptyCount, Strings.wPII, string.Empty);
                                }
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.RemoveCustomTitleProp)
                        {
                            if (Word.RemoveCustomTitleProp(tempFilePackageViewer))
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Custom Property 'Title' Removed From File.", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "'Title' Property Not Found.", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.UpdateCcNamespaceGuid)
                        {
                            if (WordFixes.FixContentControlNamespaces(tempFilePackageViewer))
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Quick Part Namespaces Updated", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "No Issues With Namespaces Found.", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelBookmarks)
                        {
                            if (Word.RemoveBookmarks(tempFilePackageViewer))
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Bookmarks Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "No Bookmarks In Document", string.Empty);
                            }
                        }

                        if (f.wdModCmd == AppUtilities.WordModifyCmds.DelDupeAuthors)
                        {
                            Dictionary<string, string> authors = new Dictionary<string, string>();

                            using (WordprocessingDocument document = WordprocessingDocument.Open(tempFilePackageViewer, true))
                            {
                                // check the peoplepart and list those authors
                                WordprocessingPeoplePart peoplePart = document.MainDocumentPart.WordprocessingPeoplePart;
                                if (peoplePart != null)
                                {
                                    foreach (Person person in peoplePart.People)
                                    {
                                        authors.Add(person.Author, person.PresenceInfo.UserId);
                                    }
                                }
                            }

                            using (var fDupe = new FrmDuplicateAuthors(authors))
                            {
                                var dupeResult = fDupe.ShowDialog();
                            }
                        }

                        if (result == DialogResult.OK)
                        {
                            string modifiedPath = AddModifiedTextToFileName(toolStripStatusLabelFilePath.Text);
                            File.Copy(tempFilePackageViewer, modifiedPath, true);
                        }
                    }
                }
                else if (StrOfficeApp == Strings.oAppExcel)
                {
                    using (var f = new FrmExcelModify())
                    {
                        DialogResult result = f.ShowDialog();

                        if (result == DialogResult.Cancel)
                        {
                            return;
                        }

                        Cursor = Cursors.WaitCursor;

                        if (f.xlModCmd == AppUtilities.ExcelModifyCmds.DelLink)
                        {
                            using (var fDelLink = new FrmExcelDelLink(tempFileReadOnly))
                            {
                                if (fDelLink.fHasLinks)
                                {
                                    fDelLink.ShowDialog();
                                    if (fDelLink.DialogResult == DialogResult.OK)
                                    {
                                        LogInformation(LogInfoType.ClearAndAdd, "Hyperlink Deleted", string.Empty);
                                    }
                                }
                            }
                        }

                        if (f.xlModCmd == AppUtilities.ExcelModifyCmds.DelLinks)
                        {
                            if (Excel.RemoveHyperlinks(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Hyperlinks Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete hyperlinks", string.Empty);
                            }
                        }

                        if (f.xlModCmd == AppUtilities.ExcelModifyCmds.DelEmbeddedLinks)
                        {
                            if (Excel.RemoveLinks(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Embedded Links Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete links", string.Empty);
                            }
                        }

                        if (f.xlModCmd == AppUtilities.ExcelModifyCmds.DelSheet)
                        {
                            using (var fds = new FrmDeleteSheet(package, tempFilePackageViewer))
                            {
                                fds.ShowDialog();

                                if (fds.sheetName != string.Empty)
                                {
                                    rtbDisplay.AppendText("Sheet: " + fds.sheetName + " Removed");
                                }
                            }
                        }

                        if (f.xlModCmd == AppUtilities.ExcelModifyCmds.DelComments)
                        {
                            if (Excel.RemoveComments(tempFilePackageViewer) == true)
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Comments Deleted", string.Empty);
                            }
                            else
                            {
                                LogInformation(LogInfoType.ClearAndAdd, "Unable to delete comments", string.Empty);
                            }
                        }

                        if (f.xlModCmd == AppUtilities.ExcelModifyCmds.ConvertXlsmToXlsx)
                        {
                            string fNewName = Office.ConvertMacroEnabled2NonMacroEnabled(tempFilePackageViewer, Strings.oAppExcel);
                            rtbDisplay.Clear();
                            if (fNewName != string.Empty)
                            {
                                rtbDisplay.AppendText(tempFilePackageViewer + Strings.convertedTo + fNewName);
                            }
                        }

                        if (f.xlModCmd == AppUtilities.ExcelModifyCmds.ConvertStrictToXlsx)
                        {
                            try
                            {
                                Cursor = Cursors.WaitCursor;

                                // check if the excelcnv.exe exists, without it, no conversion can happen
                                string excelcnvPath;

                                if (File.Exists(Strings.sameBitnessO365))
                                {
                                    excelcnvPath = Strings.sameBitnessO365;
                                }
                                else if (File.Exists(Strings.x86OfficeO365))
                                {
                                    excelcnvPath = Strings.x86OfficeO365;
                                }
                                else if (File.Exists(Strings.sameBitnessMSI2016))
                                {
                                    excelcnvPath = Strings.sameBitnessMSI2016;
                                }
                                else if (File.Exists(Strings.x86OfficeMSI2016))
                                {
                                    excelcnvPath = Strings.x86OfficeMSI2016;
                                }
                                else if (File.Exists(Strings.sameBitnessMSI2013))
                                {
                                    excelcnvPath = Strings.sameBitnessMSI2013;
                                }
                                else if (File.Exists(Strings.x86OfficeMSI2013))
                                {
                                    excelcnvPath = Strings.x86OfficeMSI2013;
                                }
                                else
                                {
                                    // if no path is found, we will be unable to convert
                                    excelcnvPath = string.Empty;
                                    rtbDisplay.AppendText("** Unable to convert file **");
                                    return;
                                }

                                // check if the file is strict, no changes are made to the file yet
                                bool isStrict = false;

                                using (Package package = Package.Open(tempFilePackageViewer, FileMode.Open, FileAccess.Read))
                                {
                                    foreach (PackagePart part in package.GetParts())
                                    {
                                        if (part.Uri.ToString() == "/xl/workbook.xml")
                                        {
                                            try
                                            {
                                                string docText = string.Empty;
                                                using (StreamReader sr = new StreamReader(part.GetStream()))
                                                {
                                                    docText = sr.ReadToEnd();
                                                    if (docText.Contains(@"conformance=""strict"""))
                                                    {
                                                        isStrict = true;
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                FileUtilities.WriteToLog(Strings.fLogFilePath, "BtnConvertToNonStrictFormat_Click ReadToEnd Error = " + ex.Message);
                                            }
                                        }
                                    }
                                }

                                // if the file is strict format
                                // run the command to convert it to non-strict
                                if (isStrict == true)
                                {
                                    // setup destination file path
                                    string strOriginalFile = tempFilePackageViewer;
                                    string strOutputPath = Path.GetDirectoryName(strOriginalFile) + "\\";
                                    string strFileExtension = Path.GetExtension(strOriginalFile);
                                    string strOutputFileName = strOutputPath + Path.GetFileNameWithoutExtension(strOriginalFile) + Strings.wFixedFileParentheses + strFileExtension;

                                    // run the command to convert the file "excelcnv.exe -nme -oice "strict-file-path" "converted-file-path""
                                    string cParams = " -nme -oice " + Strings.chDblQuote + tempFilePackageViewer + Strings.chDblQuote + Strings.wSpaceChar + Strings.chDblQuote + strOutputFileName + Strings.chDblQuote;
                                    var proc = Process.Start(excelcnvPath, cParams);
                                    proc.Close();
                                    rtbDisplay.AppendText(Strings.fileConvertSuccessful);
                                    rtbDisplay.AppendText("File Location: " + strOutputFileName);
                                }
                                else
                                {
                                    rtbDisplay.AppendText("** File Is Not Open Xml Format (Strict) **");
                                }
                            }
                            catch (Exception ex)
                            {
                                LogInformation(LogInfoType.LogException, "BtnConvertToNonStrictFormat_Click Error = ", ex.Message);
                            }
                            finally
                            {
                                Cursor = Cursors.Default;
                            }
                        }

                        if (result == DialogResult.OK)
                        {
                            string modifiedPath = AddModifiedTextToFileName(tempFilePackageViewer);
                            File.Copy(tempFilePackageViewer, modifiedPath, true);
                        }
                    }
                }
                else if (StrOfficeApp == Strings.oAppPowerPoint)
                {
                    using (var f = new FrmPowerPointModify())
                    {
                        DialogResult result = f.ShowDialog();

                        if (result == DialogResult.Cancel)
                        {
                            return;
                        }

                        Cursor = Cursors.WaitCursor;

                        if (f.pptModCmd == AppUtilities.PowerPointModifyCmds.ConvertPptmToPptx)
                        {
                            string fNewName = Office.ConvertMacroEnabled2NonMacroEnabled(tempFilePackageViewer, Strings.oAppPowerPoint);
                            rtbDisplay.Clear();
                            if (fNewName != string.Empty)
                            {
                                rtbDisplay.AppendText(tempFilePackageViewer + Strings.convertedTo + fNewName);
                            }
                        }

                        if (f.pptModCmd == AppUtilities.PowerPointModifyCmds.DelComments)
                        {
                            if (PowerPoint.DeleteComments(tempFilePackageViewer, string.Empty))
                            {
                                rtbDisplay.AppendText("Comments Removed");
                            }
                            else
                            {
                                rtbDisplay.AppendText("No Comments Removed");
                            }
                        }

                        if (f.pptModCmd == AppUtilities.PowerPointModifyCmds.RemovePIIOnSave)
                        {
                            using (PresentationDocument document = PresentationDocument.Open(tempFilePackageViewer, true))
                            {
                                document.PresentationPart.Presentation.RemovePersonalInfoOnSave = false;
                                document.PresentationPart.Presentation.Save();
                                rtbDisplay.AppendText("Remove PII On Save Disabled");
                            }
                        }

                        if (f.pptModCmd == AppUtilities.PowerPointModifyCmds.MoveSlide)
                        {
                            FrmMoveSlide mvFrm = new FrmMoveSlide(tempFileReadOnly)
                            {
                                Owner = this
                            };
                            mvFrm.ShowDialog();
                        }

                        if (result == DialogResult.OK)
                        {
                            string modifiedPath = AddModifiedTextToFileName(tempFilePackageViewer);
                            File.Copy(tempFilePackageViewer, modifiedPath, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "ModifyContents Error:", ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void editToolStripMenuItemRemoveCustomDocProps_Click(object sender, EventArgs e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                if (Office.RemoveCustomDocProperties(package, toolStripStatusLabelDocType.Text))
                {
                    LogInformation(LogInfoType.ClearAndAdd, "Custom File Properties Removed.", string.Empty);
                }
                else
                {
                    throw new Exception();
                }
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "Remove Custom Doc Props Failed", ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void editToolStripMenuItemRemoveCustomXml_Click(object sender, EventArgs e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                if (Office.RemoveCustomXmlParts(package, tempFilePackageViewer, toolStripStatusLabelDocType.Text))
                {
                    LogInformation(LogInfoType.ClearAndAdd, "Custom Xml Parts Removed.", string.Empty);
                }
                else
                {
                    LogInformation(LogInfoType.ClearAndAdd, "Document Does Not Contain Custom Xml.", string.Empty);
                }
            }
            catch (Exception ex)
            {
                LogInformation(LogInfoType.LogException, "Remove Custom Xml Failed", ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void fileToolStripMenuItemClose_Click(object sender, EventArgs e)
        {
            FileClose();
        }

        #endregion
    }
}