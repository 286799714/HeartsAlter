import { Client, Room } from "@colyseus/sdk";
import { cli, Options } from "@colyseus/loadtest";

export async function main(options: Options) {
  const client = new Client(options.endpoint);
  const room: Room = await client.joinOrCreate(options.roomName, {
    name: `压测玩家-${process.pid}`,
  });

  console.log(`joined ${options.roomName} successfully (session ${room.sessionId})`);

  room.onMessage("round_started", (payload: any) => {
    console.log(`round ${payload.roundNumber} started; pot=${payload.pot}`);
  });

  room.onStateChange((state: any) => {
    if (state.phase === "finished") {
      console.log("round finished", state.message);
    }
  });

  room.onLeave((code: number) => {
    console.log("left");
  });
}

cli(main);
