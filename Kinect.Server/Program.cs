﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Json;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using Fleck;
using Microsoft.Kinect;
using Microsoft.Speech.AudioFormat;
using Microsoft.Speech.Recognition;
using Newtonsoft.Json;
using Accord.Statistics.Distributions.Fitting;
using Accord.Statistics.Distributions.Multivariate;
using Accord.Statistics.Models.Fields;
using Accord.Statistics.Models.Fields.Functions;
using Accord.Statistics.Models.Fields.Learning;
using Accord.Statistics.Models.Markov;
using Accord.Statistics.Models.Markov.Learning;
using Accord.Statistics.Models.Markov.Topology;
using log4net;
using log4net.Config;

namespace Kinect.Server
{
    class Program
    {
        // Define a static logger variable so that it references the
        // Logger instance named "Program".
        static readonly ILog log = LogManager.GetLogger(typeof(Program));
        static Boolean debugging = true;

        static List<IWebSocketConnection> clients = new List<IWebSocketConnection>();

        #region kinect related
        static Mode mode = Mode.Body;
        /// <summary>
        /// Active Kinect sensor
        /// </summary>
        static KinectSensor kinectSensor = null;
        /// <summary>
        /// Coordinate mapper to map one type of point to another
        /// </summary>
        static CoordinateMapper coordinateMapper;
        /// <summary>
        /// Reader for body frames
        /// </summary>
        static BodyFrameReader bodyFrameReader;
        /// <summary>
        /// Array for the bodies
        /// </summary>
        static Body[] bodies;
        /// <summary>
        /// definition of bones
        /// </summary>        
        static List<Tuple<JointType, JointType>> bones;        
        /// <summary>
        /// Stream for 32b-16b conversion.
        /// </summary>
        static KinectAudioStream convertStream = null;
        /// <summary>
        /// Speech recognition engine using audio data from Kinect.
        /// </summary>
        static SpeechRecognitionEngine speechEngine = null;
        #endregion

        #region hmm and exercise recognition related
        static List<List<Point>> observationSequences; //list of all sequences for all joints
        static List<Database> databases; //each database object corresponds to observation sequence of a joint for (possibly many) labels
        static String saveDirPath;
        static String[] filenames; //filenames of joints correspond to jointNames (ex: LeftAnkle.xml)
        static List<HiddenMarkovClassifier<MultivariateNormalDistribution>> hmms; //1 hmm per joint
        static int numJoints = Enum.GetNames(typeof(JointType)).Length; //Kinect v2.0 detects 25 joints in a body
        static Dictionary<String, double> avgFramesPerLabel_Training;
        static Boolean captureStarted = false; //dictates whethere or not to save incoming frames as part of the sequence
        static Boolean training = true;
        static List<String> exercisesToMonitor;
        static Boolean alarmRinging = false;
        static SoundPlayer player = new SoundPlayer(Properties.Resources.clockBuzzer);
        static System.Threading.Timer timer;
        static DateTime alarmDateTime;
        #endregion

        static void Main(string[] args)
        {
            //log4net: Set up a simple configuration that logs on the console.
            XmlConfigurator.Configure();

            InitializeConnection();
            InitializeKinect();
            InitializeHmms();

            //type anything to quit :) its a feature ;)
            Console.ReadLine();
        }

        static void InitializeConnection()
        {
            var server = new WebSocketServer("ws://0.0.0.0:8185");
            log.Debug("web socket server started " + server);
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    clients.Add(socket);
                };

                socket.OnClose = () =>
                {
                    clients.Remove(socket);
                };

                socket.OnMessage = message => { 
                    messageHandler((string)message, socket); 
                };
            });
        }

        /// <summary>
        /// Handles messages from client socket
        /// </summary>
        /// <param name="message"></param>
        static void messageHandler(String message, IWebSocketConnection socket){
           
            log.Debug("RECEIVED message from client: " + socket + ", message: " + message);
            JsonValue jsonMessage = JsonValue.Parse(message);
            String page = (string)jsonMessage["page"];
            String operation;
            String data;
            switch (page)
            {       
                case "home":
                    operation = (string)jsonMessage["operation"];
                    if (operation == "mode")
                    {
                        training = false; //if on home page, we are not in training mode.
                        //log.Debug("training is set to " + training);
                    }
                    break;
                case "train":
                    operation = (string)jsonMessage["operation"];
                    switch (operation)
                    {
                        case "accept":
                            String label = (string)jsonMessage["data"]["label"];
                            addExercise(label);
                            sendNotification("Training case for exercise : " + label + " added.");
                            
                            break;
                        case "reject":
                            log.Debug("length of obs sequences before rejecting " + observationSequences[0].Count());
                            clearObservations();
                            sendNotification("Training case rejected!");
                            log.Debug("length of obs sequences before rejecting " + observationSequences[0].Count());
                            
                            break;
                        case "train":
                            sendNotification("Training...");
                            learnHmms();
                            sendNotification("Training finished!");
                            break;
                        case "mode":
                            training = true; //if on train page, we ARE in training mode
                            //log.Debug("training is set to " + training);
                            break;
                        case "reset":
                            resetTrainingData();
                            sendNotification("Cleared all existing training cases");
                            break;
                        case "labels":
                            if (avgFramesPerLabel_Training != null && avgFramesPerLabel_Training.Count() > 0)
                            {
                                List<String> labels = new List<string>(avgFramesPerLabel_Training.Keys);
                                data = JsonConvert.SerializeObject(labels);
                                broadcastMessage(createJsonString(page, operation, data));
                            }                                    
                            break;
                        default:
                            break;
                    }
                    break;
                case "options":
                    int hours = Convert.ToInt32((string)jsonMessage["data"]["hour"]);
                    int minutes = Convert.ToInt32((string)jsonMessage["data"]["minute"]);
                    DateTime now = DateTime.Now;
                    alarmDateTime = new DateTime(now.Year, now.Month, now.Day, hours, minutes, 0);
                    log.Debug("alarmDateTime set to " + alarmDateTime);
                    TimeSpan alarmGoesOffIn = alarmDateTime.TimeOfDay - now.TimeOfDay;
                    log.Debug("alarm should go off in " + alarmGoesOffIn.Hours + " hours, " + alarmGoesOffIn.Minutes + " minutes, and " + alarmGoesOffIn.Seconds + " seconds");
                    if (setAlarm(alarmGoesOffIn))
                    {
                        String excsRecd = (string)jsonMessage["data"]["exc"];
                        log.Debug("number of exercises in exercisesToMonitor list: " + exercisesToMonitor.Count());
                        exercisesToMonitor = excsRecd.Split(Constants.DELIMETER).ToList();
                        log.Debug("number of exercises in exercisesToMonitor list after receiving update from client: " + exercisesToMonitor.Count());
                    }
                    break;
                default:
                    break;
            }
                
        }

        static void resetTrainingData()
        {
            if (Directory.Exists(saveDirPath))
            {
                Directory.Delete(saveDirPath, true);
            }
            
            for (int i = 0; i < databases.Count();i++ )
            {
                databases[i].Clear();
            }

            clearObservations();
            if (avgFramesPerLabel_Training != null)
            {
                avgFramesPerLabel_Training.Clear();
            }
            
        }

        public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp);
            return dtDateTime;
        }

        static String createJsonString(String page, String operation, String data)
        {
            String json = "{\"page\":\"" + page + "\",\"operation\":\"" + operation + "\",\"data\":" + data + "}";
            return json;
        }

        /// <summary>
        /// Send json message across to all the websocket clients
        /// </summary>
        /// <param name="json"></param>
        static void broadcastMessage(String json)
        {
            foreach (var socket in clients)
            {
                //log.Debug("SENDING: " + json);
                socket.Send(json);
            }
        }

        static void InitializeKinect()
        {
            kinectSensor = KinectSensor.GetDefault();
            coordinateMapper = kinectSensor.CoordinateMapper;
            if (kinectSensor != null)
            {
                switch (mode)
                {
                    case Mode.Body:
                        bodyFrameReader = kinectSensor.BodyFrameSource.OpenReader();
                        InitializeJoints();                        
                        kinectSensor.Open();

                        if (bodyFrameReader != null)
                        {
                            bodyFrameReader.FrameArrived += Reader_FrameArrived;
                        }
                        break;
                    default:
                        break;
                }
                
                InitializeSpeechRecognition();                
            }
        }
                       
        static void InitializeJoints()
        {
            bones = new List<Tuple<JointType, JointType>>();
            // Torso
            bones.Add(new Tuple<JointType, JointType>(JointType.Head, JointType.Neck));
            bones.Add(new Tuple<JointType, JointType>(JointType.Neck, JointType.SpineShoulder));
            bones.Add(new Tuple<JointType, JointType>(JointType.SpineShoulder, JointType.SpineMid));
            bones.Add(new Tuple<JointType, JointType>(JointType.SpineMid, JointType.SpineBase));
            bones.Add(new Tuple<JointType, JointType>(JointType.SpineShoulder, JointType.ShoulderRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.SpineShoulder, JointType.ShoulderLeft));
            bones.Add(new Tuple<JointType, JointType>(JointType.SpineBase, JointType.HipRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.SpineBase, JointType.HipLeft));

            // Right Arm
            bones.Add(new Tuple<JointType, JointType>(JointType.ShoulderRight, JointType.ElbowRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.ElbowRight, JointType.WristRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.WristRight, JointType.HandRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.HandRight, JointType.HandTipRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.WristRight, JointType.ThumbRight));

            // Left Arm
            bones.Add(new Tuple<JointType, JointType>(JointType.ShoulderLeft, JointType.ElbowLeft));
            bones.Add(new Tuple<JointType, JointType>(JointType.ElbowLeft, JointType.WristLeft));
            bones.Add(new Tuple<JointType, JointType>(JointType.WristLeft, JointType.HandLeft));
            bones.Add(new Tuple<JointType, JointType>(JointType.HandLeft, JointType.HandTipLeft));
            bones.Add(new Tuple<JointType, JointType>(JointType.WristLeft, JointType.ThumbLeft));

            // Right Leg
            bones.Add(new Tuple<JointType, JointType>(JointType.HipRight, JointType.KneeRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.KneeRight, JointType.AnkleRight));
            bones.Add(new Tuple<JointType, JointType>(JointType.AnkleRight, JointType.FootRight));

            // Left Leg
            bones.Add(new Tuple<JointType, JointType>(JointType.HipLeft, JointType.KneeLeft));
            bones.Add(new Tuple<JointType, JointType>(JointType.KneeLeft, JointType.AnkleLeft));
            bones.Add(new Tuple<JointType, JointType>(JointType.AnkleLeft, JointType.FootLeft));
        }
               
        static void Reader_FrameArrived(object sender, BodyFrameArrivedEventArgs e)
        {

            using (BodyFrame bodyFrame = e.FrameReference.AcquireFrame())
            {
                bool dataReceived = false;
                if (bodyFrame != null)
                {
                    if (bodies == null)
                    {
                        bodies = new Body[bodyFrame.BodyCount];
                    }

                    // The first time GetAndRefreshBodyData is called, Kinect will allocate each Body in the array.
                    // As long as those body objects are not disposed and not set to null in the array,
                    // those body objects will be re-used.
                    bodyFrame.GetAndRefreshBodyData(bodies);
                    dataReceived = true;
                }

                if (dataReceived)
                {
                    var users = bodies.Where(s => s.IsTracked).ToList();
                    if (users.Count > 0)
                    {
                        // prepare data to send for JSON serialization. In particular, send skeleton data, frameCount, avgFrameCountForCurrExc, and list of remaining exercises
                        Dictionary<JointType, Point> jointPoints = getJointPoints(users, coordinateMapper);

                        if (captureStarted)
                        {
                            //Add all joint locations to observationSequences
                            for (int i = 0; i < observationSequences.Count; i++)
                            {
                                observationSequences[i].Add(jointPoints[((JointType)i)]);
                            }

                            //if not training, then in testing => check for exercise
                            if (!training)
                            {
                                checkForExercises();
                            }

                            sendSkeletonAndExerciseData(users[0], jointPoints);
                        }

                        if (alarmRinging==true && exercisesToMonitor.Count() == 0)
                        {
                            stopAlarm();
                        }
                    }
                }
            }
        }

        static void stopAlarm()
        {
            alarmRinging = false;
            player.Stop();
            stopCapture();
            sendNotification("Good Morning! :D");
        }

        static void sendNotification(String notification)
        {
            sendBasicMessage("home", "notification", notification);
        }

        static void sendBasicMessage(String page, String operation, String message)
        {
            String data = JsonConvert.SerializeObject(message);
            broadcastMessage(createJsonString(page, operation, data));
        }

        /// <summary>
        /// Send data to the client home page about location of joints for currently tracked skeleton, as well as state of current exercise being monitored
        /// </summary>
        /// <param name="skeleton"></param>
        /// <param name="jointPoints"></param>
        static void sendSkeletonAndExerciseData(Body skeleton, Dictionary<JointType, Point> jointPoints)
        {
            int frameCount = observationSequences.Count > 0 ? observationSequences[0].Count() : 0;
            double avgFramesForCurrExc = 0;
            if (exercisesToMonitor.Count() > 0)
            {
                if (avgFramesPerLabel_Training != null)
                {
                    avgFramesForCurrExc = avgFramesPerLabel_Training.ContainsKey(exercisesToMonitor.ElementAt(0)) ? avgFramesPerLabel_Training[exercisesToMonitor.ElementAt(0)] : 0;
            
                }
            }
            String json = CustomJsonSerializer.SerializeHomePageData(skeleton, jointPoints, alarmRinging, avgFramesForCurrExc, frameCount, exercisesToMonitor);
            broadcastMessage(json);
        }

        /// <summary>
        /// Send observation sequences to be animated on the front end canvas, after a user has done a training sample
        /// </summary>
        static void sendObservationSequences()
        {
            String data = CustomJsonSerializer.SerializeObservationSequences(observationSequences);
            broadcastMessage(createJsonString("train", "animate", data));
        }

        /// <summary>
        /// For the body frame of the first tracked skeleton, get a dictionary of all jointTypes and their corresponding currentPoint
        /// </summary>
        /// <param name="skeletons"></param>
        /// <param name="mapper"></param>
        /// <returns></returns>
        static Dictionary<JointType, Point> getJointPoints(List<Body> skeletons, CoordinateMapper mapper)
        {
            Body skeleton = skeletons[0]; //only consider the first tracked skeleton.
            IReadOnlyDictionary<JointType, Joint> joints = skeleton.Joints;
            Dictionary<JointType, Point> jointPoints = new Dictionary<JointType, Point>();
            foreach (JointType jointType in joints.Keys)
            {
                // sometimes the depth(Z) of an inferred joint may show as negative
                // clamp down to 0.1f to prevent coordinatemapper from returning (-Infinity, -Infinity)
                CameraSpacePoint position = joints[jointType].Position;
                if (position.Z < 0)
                {
                    position.Z = Constants.InferredZPositionClamp;
                }

                DepthSpacePoint depthSpacePoint = mapper.MapCameraPointToDepthSpace(position);
                jointPoints[jointType] = new Point((int)depthSpacePoint.X, (int)depthSpacePoint.Y);
            }
            return jointPoints;
        }

        /// <summary>
        /// Gets the metadata for the speech recognizer (acoustic model) most suitable to
        /// process audio from Kinect device.
        /// </summary>
        /// <returns>
        /// RecognizerInfo if found, <code>null</code> otherwise.
        /// </returns>
        static RecognizerInfo TryGetKinectRecognizer()
        {
            IEnumerable<RecognizerInfo> recognizers;

            // This is required to catch the case when an expected recognizer is not installed.
            // By default - the x86 Speech Runtime is always expected. 
            try
            {
                recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            }
            catch (COMException)
            {
                return null;
            }

            foreach (RecognizerInfo recognizer in recognizers)
            {
                string value;
                recognizer.AdditionalInfo.TryGetValue("Kinect", out value);
                if ("True".Equals(value, StringComparison.OrdinalIgnoreCase) && "en-US".Equals(recognizer.Culture.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return recognizer;
                }
            }

            return null;
        }

        static void InitializeSpeechRecognition()
        {
            // grab the audio stream
            IReadOnlyList<AudioBeam> audioBeamList = kinectSensor.AudioSource.AudioBeams;
            System.IO.Stream audioStream = audioBeamList[0].OpenInputStream();

            // create the convert stream
            convertStream = new KinectAudioStream(audioStream);

            RecognizerInfo ri = TryGetKinectRecognizer();

            if (null != ri)
            {
                speechEngine = new SpeechRecognitionEngine(ri.Id);

                var directions = new Choices();
                directions.Add(new SemanticResultValue("start", "START"));
                directions.Add(new SemanticResultValue("stop", "STOP"));

                var gb = new GrammarBuilder { Culture = ri.Culture };
                gb.Append(directions);
                var g = new Grammar(gb);
                speechEngine.LoadGrammar(g);

                speechEngine.SpeechRecognized += SpeechRecognized;
                speechEngine.SpeechRecognitionRejected += SpeechRejected;

                // let the convertStream know speech is going active
                convertStream.SpeechActive = true;

                // For long recognition sessions (a few hours or more), it may be beneficial to turn off adaptation of the acoustic model. 
                // will prevent recognition accuracy from degrading over time.
                ////speechEngine.UpdateRecognizerSetting("AdaptationOn", 0);


                speechEngine.SetInputToAudioStream(
                    convertStream, new SpeechAudioFormatInfo(EncodingFormat.Pcm, 16000, 16, 1, 32000, 2, null));
                speechEngine.RecognizeAsync(RecognizeMode.Multiple);
            }
            else
            {
                //statusBarText.Text = Properties.Resources.NoSpeechRecognizer;
            }
        }

        static void SpeechRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            //throw new NotImplementedException();
        }

        /// <summary>
        /// Handler for recognized speech events.
        /// </summary>
        /// <param name="sender">object sending the event.</param>
        /// <param name="e">event arguments.</param>
        static void SpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            // Speech utterance confidence below which we treat speech as if it hadn't been heard
            const double ConfidenceThreshold = 0.3;           

            // this.ClearRecognitionHighlights();

            if (e.Result.Confidence >= ConfidenceThreshold)
            {
                switch (e.Result.Semantics.Value.ToString())
                {
                    case "START":
                        if (training) //only relevant while training
                        {
                            log.Info("START voice command received");
                            startCapture();
                        }
                        break;

                    case "STOP":
                        if (training)
                        {
                            log.Info("STOP voice command received");
                            stopCapture();
                            sendObservationSequences();
                            
                        }                                               
                        break;
                }
            }
        }
        
        static void InitializeHmms()
        {
            saveDirPath = new Uri(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase), Constants.TRAINING_DATA_DIR)).LocalPath;            
            observationSequences = new List<List<Point>>(); //store locations all points for all joints for an observation
            databases = new List<Database>();
            filenames = new String[numJoints];
            hmms = new List<HiddenMarkovClassifier<MultivariateNormalDistribution>>();            
            Directory.CreateDirectory(saveDirPath); //create dir if it doesn't already exist

            for (int i = 0; i < numJoints; i++)
            {
                observationSequences.Add(new List<Point>());
                filenames[i] = ((JointType)i).ToString() + ".xml"; //initialize file names
                hmms.Add(null);
                databases.Add(new Database());
            }

            //if there are files in the saveDirPath, load all the files            
            if (Directory.GetFiles(saveDirPath, "*", SearchOption.TopDirectoryOnly).Length == filenames.Length){
                loadDatabases();
                log.Debug("loaded training data from all joints from directory: " + saveDirPath);
            }
            else
            {
                log.Warn("No files in " + saveDirPath + "! Are you not `trained` yet?");
            }
            
            exercisesToMonitor = new List<String>();
        }

        /**
         * Load observation sequences for each joint file
         */
        static void loadDatabases()
        {
            String currFilePath = "";
            for (int i = 0; i < numJoints; i++)
            {                                
                currFilePath = Path.Combine(saveDirPath, Path.GetFileName(filenames[i]));
                databases[i].Load(new FileStream(currFilePath, FileMode.Open)); //load databases if training data already exists                
            }
            //training data has been loaded, now learn the HMM models for each joint in training data
            learnHmms();
        }

        /// <summary>
        /// Set capturing flag to true so that kinect frames are saved
        /// </summary>
        static void startCapture()
        {
            log.Debug("Capture started");
            captureStarted = true;
        }

        /// <summary>l
        /// Stop saving kinect frames. If training mode is on, simply keep the observations and wait for client to tell us what to do with is
        /// If testing, clear observations.
        /// </summary> 
        static void stopCapture()
        {
            log.Debug("Capture stopped");
            captureStarted = false;
            if (!training){ //if training, dont clear observations right away - let user decide if they want to accept / reject it before clearing
                clearObservations();
            }
        }

        static void checkForExercises()
        {
            if (exercisesToMonitor.Count > 0){
                String currExc = exercisesToMonitor.ElementAt(0);               
                log.Debug("Checking for exercise " +currExc+ " in the current observation sequences");
                if (exerciseFound(currExc))
                {
                    log.Debug(currExc + " found! Removing it from list of exercises to monitor.");
                    exercisesToMonitor.RemoveAt(0);
                    clearObservations();
                }
            }
            else{
                log.Warn("there were no exercises to monitor!");
            }
        }

        /// <summary>
        /// Does hmm comput to see if exercise was found or not
        /// </summary>
        /// <param name="exerciseToCheck"></param>
        /// <returns></returns>
        static Boolean exerciseFound(String exerciseToCheck)
        {
            if (!avgFramesPerLabel_Training.ContainsKey(exerciseToCheck)){
                log.Error("Invalid exercise name! " + exerciseToCheck + " not found in average frames per label dictionary");
                return false;
            }

            // only consider exercises if the number of frames are within a threshold
            double frameCount = observationSequences[0].Count();
            double avgFrameCountForExc = avgFramesPerLabel_Training[exerciseToCheck] ;
            double frameDifference = avgFrameCountForExc - frameCount;
            double frameDifferenceThreshold = Convert.ToInt32(avgFrameCountForExc * Constants.FRAME_THRESHOLD_PERCENT);
            
            if (Math.Abs(frameDifference) > frameDifferenceThreshold)
            {
                if (frameCount > avgFrameCountForExc)
                {
                    clearObservations();
                }
                return false;
            }
            if (debugging)
            {
                log.Debug("Frames DIfference is: " + frameDifference + 
                    ", avg frames for exercise: " + exerciseToCheck + " is: " + avgFrameCountForExc + 
                    ", Frames Threshold is " + frameDifferenceThreshold + 
                    ", Frame Differ Count is: " + frameCount);
            }
            
            //prepare for hmm computations
            double[][][] inputs = new Double[numJoints][][];
            for (int i = 0; i < numJoints; i++)
            {
                inputs[i] = Sequence.Preprocess(observationSequences[i].ToArray());
            }

            //do hmm computation to find closest exercise match for each joint
            String[] outputLabels = new String[inputs.Length];
            String label;
            for (int i = 0; i < inputs.Length; i++)
            {
                int index = hmms[i].Compute(inputs[i]);
                label = (index >= 0) ? databases[i].Classes[index] : "NOT FOUND";
                outputLabels[i] = label;
            }

            //determine if exercise is done by analyzing evidence in each joint for the given exercise
            if (outputLabels.Any(x => x != "NOT FOUND"))
            {
                //return most occuring exercise label by considering labels for all the joints
                label = outputLabels.Where(x => x != "NOT FOUND").GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key;
                int numLabels = outputLabels.Where(x => x == label).Count();

                if (numLabels < Constants.MIN_NUM_MATCHED_JOINTS)
                {
                    label = "NOT FOUND";
                }
            }
            else
            {
                label = "NOT FOUND";
            }

            if (label == exerciseToCheck)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reset observation sequences so can capture the next set of data
        /// </summary>
        static void clearObservations()
        {
            for (int i = 0; i < observationSequences.Count(); i++)
            {
                observationSequences[i].Clear();
            }
                
        }

        /// <summary>
        /// Use training data to learn hmm models for all the joints
        /// </summary>
        static void learnHmms(){
            if (databases[0].Samples.Count <= 0)
            {
                log.Debug("no samples in the database, skipping hmm model learning and computations");
                return;
            }
            kinectSensor.Close();
            for (int i = 0; i < databases.Count; i++)
            {
                hmms[i] = learnHMM(databases[i]);
               log.Debug("done learning hmm for joint: " + i + " : " + Enum.GetName(typeof(JointType), i)); 
            }
            saveDatabasesToFiles();

            //set average frames per label dictionary
            avgFramesPerLabel_Training = databases[0].avgFramesPerLabel();

            kinectSensor.Open();
        }

        /// <summary>
        /// learn Hmm model for samples in given database using Baum Welch unsupervised learning algorithm
        /// then used the learned model to classify the training samples
        /// </summary>
        /// <param name="database"></param>
        /// <returns></returns>
        static HiddenMarkovClassifier<MultivariateNormalDistribution> learnHMM(Database database)
        {

            BindingList<Sequence> samples = database.Samples;
            BindingList<String> classes = database.Classes;

            double[][][] inputs = new double[samples.Count][][];
            int[] outputs = new int[samples.Count];

            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = samples[i].Input;
                outputs[i] = samples[i].Output;
            }

            int states = 5;
            int iterations = 0;
            double tolerance = 0.1;
            bool rejection = true;


            HiddenMarkovClassifier<MultivariateNormalDistribution> hmm = new HiddenMarkovClassifier<MultivariateNormalDistribution>(classes.Count,
                new Forward(states), new MultivariateNormalDistribution(2), classes.ToArray());

            // Create the learning algorithm for the ensemble classifier
            var teacher = new HiddenMarkovClassifierLearning<MultivariateNormalDistribution>(hmm,

                // Train each model using the selected convergence criteria
                i => new BaumWelchLearning<MultivariateNormalDistribution>(hmm.Models[i])
                {
                    Tolerance = tolerance,
                    Iterations = iterations,

                    FittingOptions = new NormalOptions()
                    {
                        Regularization = 1e-5
                    }
                }
            );

            teacher.Empirical = true;
            teacher.Rejection = rejection;


            // Run the learning algorithm
            double error = teacher.Run(inputs, outputs);

            // Classify all training instances
            foreach (var sample in database.Samples)
            {
                sample.RecognizedAs = hmm.Compute(sample.Input);
            }

            return hmm;
        }
        
        /// <summary>
        /// Remove initial num of frames from current training sample that is being added to databases
        /// </summary>
        static void removeInitialFramesFromTrainingSamples()
        {
            for (int i = 0; i < numJoints; i++)
            {
                observationSequences[i].RemoveRange(0, Constants.NUM_INITIAL_FRAMES_TO_REMOVE);
            }
        }

        /// <summary>
        /// Add a training sample to the databases of samples for all joints
        /// </summary>
        /// <param name="label"></param>
        static void addExercise(String label)
        {
            removeInitialFramesFromTrainingSamples();
            for (int i = 0; i < databases.Count; i++)
            {
                databases[i].Add(observationSequences[i].ToArray(), label);               
            }
            avgFramesPerLabel_Training = databases[0].avgFramesPerLabel();
            clearObservations();
            log.Debug("added sample for exercise: " + label + " to databases, updated avgFramesPerLabel dict, and clearing observations");
        }

        /// <summary>
        /// save training joint sample to files
        /// done after learnHmms has been called
        /// </summary>
        static void saveDatabasesToFiles()
        {
            Directory.CreateDirectory(saveDirPath); //create dir if it doesn't already exist
            String path;
            for (int i = 0; i < databases.Count; i++)
            {                
                path = Path.Combine(saveDirPath, Path.GetFileName(filenames[i]));
                log.Debug("Saving database to path: " + path);
                using (var stream = File.OpenWrite(path))
                //using (var stream = File.Create(path))
                    databases[i].Save(stream);
            }
        }

        /// <summary>
        /// Set Timer based on timespan parameter
        /// </summary>
        /// <param name="ts"></param>
        static Boolean setAlarm(TimeSpan ts)
        {
            log.Debug("setting alarm with timespan: " + ts + "which has " + ts.TotalHours + " of total hours and " + ts.TotalMinutes + " of total minutes");
            if (timer != null) timer.Dispose();
            if (ts.Seconds <= 0)
            {
                sendNotification("Y u no send me proper alarm time?");
                return false;
            }
            else
            {
                timer = new System.Threading.Timer(new System.Threading.TimerCallback(ringAlarm), null, ts, TimeSpan.FromHours(24));
                sendNotification("Settings set successfully");
                
                return true;
            }
            
        }

        /// <summary>
        /// Callback for when timer reaches target time
        /// Ring the alarm, set alarmRinging to true, and notify clients
        /// </summary>
        /// <param name="state"></param>
        static void ringAlarm(object state)
        {
            sendBasicMessage("home", "switch", "");
            log.Info("RING! RING!");
            player.PlayLooping();
            alarmRinging = true;
            startCapture(); //start monitoring for exercises now!
        }
    }
}
