console.error("BODY-STDERR");
process.stdout.write(JSON.stringify({ url: "http://127.0.0.1:1" }) + "\nBODY-STDOUT\n");
process.stdin.resume();
process.stdin.on("end", () => {
    console.log("FINAL-STDOUT");
    console.error("FINAL-STDERR");
});
