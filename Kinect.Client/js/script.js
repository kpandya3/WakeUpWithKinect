window.onload = function () {
    var status = document.getElementById("status");
    var canvas = document.getElementById("canvas");
    var context = canvas.getContext("2d");    

    if (!window.WebSocket) {
        status.innerHTML = "Your browser does not support web sockets!";
        return;
    }

    status.innerHTML = "Connecting to server...";

    // Initialize a new web socket.
    var socket = new WebSocket("ws://127.0.0.1:8185");

    // Connection established.
    socket.onopen = function () {
        status.innerHTML = "Connection successful.";
    };

    // Connection closed.
    socket.onclose = function () {
        status.innerHTML = "Connection closed.";
    }

    // Receive data FROM the server!
    socket.onmessage = function (event) {
        if (typeof event.data === "string") {
            // SKELETON DATA

            //clear canvas!
            context.clearRect(0, 0, canvas.width, canvas.height);

            // Get the data in JSON format.
            var jsonObject = JSON.parse(event.data);

            // Display the skeleton joints.
            for (var i = 0; i < jsonObject.skeletons.length; i++) {
                for (var j = 0; j < jsonObject.skeletons[i].joints.length; j++) {
                    var joint = jsonObject.skeletons[i].joints[j];                    
                    // Draw!!!
                    context.strokeStyle = "#0000FF";
                    context.fillStyle = "#0000FF";
                    context.beginPath();
                    context.arc(joint.x, joint.y, 2, 0, Math.PI * 2, true);
                    context.stroke();
                }
            }
        }
    };

};