//
//  NeonSpeech.mm
//  iOS native TTS (AVSpeechSynthesizer) + Speech Recognition (SFSpeechRecognizer) bridge
//
//  See docs/17_iOS_Platform_Architecture.md (IOS-05)
//
//  C# side calls:
//  NeonSpeech_Speak(text, "WebSpeechBridge");
//  NeonSpeech_StartRecognition("WebSpeechBridge");
//
//  Callbacks to C#:
//  UnitySendMessage(goName, "OnIOSSpeechRecognized", text);
//  UnitySendMessage(goName, "OnIOSPlaybackComplete", "");
//

#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <Speech/Speech.h>

extern "C" {

    // Simple availability stub - always true for iOS builds (real check can be added)
    int NeonSpeech_IsAvailable() {
        return 1;
    }

    void NeonSpeech_Speak(const char* text, const char* gameObjectName) {
        NSString *utteranceText = [NSString stringWithUTF8String:text ?: @""];
        NSString *goName = [NSString stringWithUTF8String:gameObjectName ?: @""];

        if (utteranceText.length == 0 || goName.length == 0) {
            return;
        }

        NSLog(@"[NeonSpeech] Speak requested: \"%@\" -> %@", utteranceText, goName);

        AVSpeechUtterance *utterance = [AVSpeechUtterance speechUtteranceWithString:utteranceText];
        utterance.voice = [AVSpeechSynthesisVoice voiceWithLanguage:@"en-US"];
        utterance.rate = AVSpeechUtteranceDefaultSpeechRate;

        AVSpeechSynthesizer *synthesizer = [[AVSpeechSynthesizer alloc] init];

        // Use a simple delegate or completion block simulation
        // For production: implement AVSpeechSynthesizerDelegate
        [synthesizer speakUtterance:utterance];

        // Simulate completion after estimated duration (better: use delegate in full impl)
        double duration = utteranceText.length * 0.08; // rough estimate
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(duration * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
            UnitySendMessage([goName UTF8String], "OnIOSPlaybackComplete", "");
        });
    }

    void NeonSpeech_StopSpeaking() {
        NSLog(@"[NeonSpeech] StopSpeaking requested");
        // In full impl: keep reference to synthesizer and call stopSpeakingAtBoundary
    }

    void NeonSpeech_StartRecognition(const char* gameObjectName) {
        NSString *goName = [NSString stringWithUTF8String:gameObjectName ?: @""];
        NSLog(@"[NeonSpeech] StartRecognition for %@", goName);

        // Request authorization (non-blocking)
        [SFSpeechRecognizer requestAuthorization:^(SFSpeechRecognizerAuthorizationStatus status) {
            if (status != SFSpeechRecognizerAuthorizationStatusAuthorized) {
                NSLog(@"[NeonSpeech] Speech recognition not authorized");
                return;
            }

            // Placeholder: real implementation would create SFSpeechRecognizer + recognition task
            // and stream results via UnitySendMessage(goName, "OnIOSSpeechRecognized", recognizedText);
            // For now we log readiness. Full streaming logic requires AVAudioEngine + delegate.
            NSLog(@"[NeonSpeech] Speech recognizer authorized and ready (full streaming pending in IOS-05)");
        }];
    }

    void NeonSpeech_StopRecognition() {
        NSLog(@"[NeonSpeech] StopRecognition");
        // Stop any active SFSpeechRecognitionTask
    }

} // extern "C"
